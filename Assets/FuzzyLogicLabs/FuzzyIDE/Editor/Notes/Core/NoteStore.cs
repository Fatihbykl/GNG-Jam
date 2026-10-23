using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// All notes of all vaults: loading, saving (conflict-safe), trash, rename/move with link updates and
    /// external change detection. The primary vault is the project's <c>.fuzzynotes</c> folder.
    /// </summary>
    public partial class NoteStore
    {
        private static NoteStore s_Instance;
        public static NoteStore Instance => s_Instance ?? (s_Instance = new NoteStore());
        /// <summary>True once <see cref="Instance"/> exists; asset callbacks use it to avoid loading every vault on import.</summary>
        public static bool HasInstance => s_Instance != null;

        public static event Action OnNotesChanged;
        public static event Action<NoteItem> OnActiveNoteChanged;
        public static event Action<NoteItem> OnNoteSaved;
        /// <summary>A note's content was replaced from disk (external edit, conflict resolution, link refactor).</summary>
        public static event Action<NoteItem> OnNoteReloaded;
        /// <summary>A note changed on disk while it had unsaved edits; saving is blocked until resolved.</summary>
        public static event Action<NoteItem> OnNoteConflict;

        private struct DeletedEntry
        {
            public string TrashPath;
            public string OriginalPath;
        }

        private readonly List<NoteItem> m_Notes = new List<NoteItem>();
        private readonly List<NoteVault> m_Vaults = new List<NoteVault>();
        private NoteItem m_ActiveNote;
        private readonly NoteIndex m_Index = new NoteIndex();
        private readonly HashSet<NoteItem> m_PendingSaves = new HashSet<NoteItem>();
        private readonly Stack<DeletedEntry> m_Deleted = new Stack<DeletedEntry>();
        private readonly List<string> m_Recent = new List<string>();
        private readonly bool m_EditorServices;
        private bool m_Loaded;
        private List<string> m_ProjectExcludes = new List<string>();

        private double m_LastEditTime;
        private const double AutoSaveDebounceSeconds = 0.5;
        private const int MaxRecent = 30;
        private const string RecentSessionKey = "FuzzyNotes.Recent";

        public IReadOnlyList<NoteItem> Notes => m_Notes;
        public IReadOnlyList<NoteVault> Vaults => m_Vaults;
        public NoteVault PrimaryVault => m_Vaults[0];
        public NoteItem ActiveNote => m_ActiveNote;
        public NoteIndex Index => m_Index;
        public string RootDirectory => PrimaryVault.Root;
        public string TrashDirectory => PrimaryVault.TrashDirectory;
        public bool CanRestoreDeleted => m_Deleted.Count > 0;

        /// <summary>Paths of recently opened notes, most recent first.</summary>
        public IReadOnlyList<string> RecentPaths => m_Recent;

        public static string NotesDirectory
        {
            get
            {
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(projectRoot, ".fuzzynotes");
            }
        }

        public static string NotesAssetsDirectory => Path.Combine(NotesDirectory, "assets");

        public NoteStore() : this(NotesDirectory, attachEditorServices: true)
        {
        }

        /// <param name="rootDirectory">Primary vault folder holding the .md files.</param>
        /// <param name="attachEditorServices">
        /// Hooks autosave, file watching, configured extra vaults and domain-reload flushing into the editor.
        /// Tests pass false and drive <see cref="FlushPendingSaves"/> / <see cref="CheckExternalChanges"/> themselves.
        /// </param>
        public NoteStore(string rootDirectory, bool attachEditorServices)
        {
            m_EditorServices = attachEditorServices;
            m_Vaults.Add(new NoteVault(NoteVault.ProjectId, "Project Notes", rootDirectory, isPrimary: true));
            EnsureStorage();

            if (!attachEditorServices) return;

            foreach (var p in SessionState.GetString(RecentSessionKey, "").Split('\n'))
                if (!string.IsNullOrWhiteSpace(p)) m_Recent.Add(p);

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting -= FlushPendingSaves;
            EditorApplication.quitting += FlushPendingSaves;

            StartWatching(PrimaryVault);
            ApplyVaultSettings();
        }

        // =====================================================================
        // Vaults
        // =====================================================================

        /// <summary>Adds (or returns) a vault. Notes are picked up by the next <see cref="LoadAllNotes"/>.</summary>
        public NoteVault AddVault(string id, string name, string root)
        {
            var existing = m_Vaults.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase) ||
                                                        string.Equals(v.Root, Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var vault = new NoteVault(id, name, root, isPrimary: false);
            // The Assets folder exists already, and its changes come from the AssetDatabase instead of a file watcher.
            if (!vault.IsProjectFiles) Directory.CreateDirectory(root);
            m_Vaults.Add(vault);
            if (m_EditorServices && !vault.IsProjectFiles) StartWatching(vault);
            return vault;
        }

        public void RemoveVault(string id)
        {
            var vault = GetVault(id);
            if (vault == null || vault.IsPrimary) return;

            FlushPendingSaves();
            vault.Watcher?.Dispose();
            m_Vaults.Remove(vault);
            foreach (var note in m_Notes.Where(n => n.VaultId == vault.Id).ToList()) RemoveFromList(note);
            RaiseNotesChanged();
        }

        public NoteVault GetVault(string idOrName) =>
            m_Vaults.FirstOrDefault(v => string.Equals(v.Id, idOrName, StringComparison.OrdinalIgnoreCase)) ??
            m_Vaults.FirstOrDefault(v => string.Equals(v.Name, idOrName, StringComparison.OrdinalIgnoreCase));

        public NoteVault VaultOf(NoteItem note) => note == null ? null : GetVault(note.VaultId);

        /// <summary>Vault containing a path (the deepest root wins when vaults are nested).</summary>
        public NoteVault VaultOfPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            NoteVault best = null;
            foreach (var v in m_Vaults)
            {
                if (v.Contains(path) && (best == null || v.Root.Length > best.Root.Length)) best = v;
            }
            return best;
        }

        /// <summary>Syncs the personal/external vaults with <see cref="NoteSettings"/> and reloads.</summary>
        public void ReloadVaultConfiguration()
        {
            ApplyVaultSettings();
            LoadAllNotes();
        }

        private void ApplyVaultSettings()
        {
            var wanted = new List<(string id, string name, string path)>();
            if (NoteSettings.PersonalVaultEnabled && !string.IsNullOrWhiteSpace(NoteSettings.PersonalVaultPath))
                wanted.Add((NoteVault.PersonalId, "Personal", NoteSettings.PersonalVaultPath));
            foreach (var path in NoteSettings.ExternalVaults)
            {
                if (Directory.Exists(path)) wanted.Add(("ext:" + Path.GetFileName(path.TrimEnd('\\', '/')), Path.GetFileName(path.TrimEnd('\\', '/')), path));
            }
            m_ProjectExcludes = NoteSettings.ProjectMarkdownExcludes;
            if (NoteSettings.ShowProjectMarkdown)
                wanted.Add((NoteVault.ProjectFilesId, "Assets", Application.dataPath));

            foreach (var v in m_Vaults.Where(v => !v.IsPrimary && !wanted.Any(w => string.Equals(w.id, v.Id, StringComparison.OrdinalIgnoreCase))).ToList())
                RemoveVault(v.Id);

            foreach (var w in wanted)
            {
                try { AddVault(w.id, w.name, w.path); }
                catch (Exception ex) { Debug.LogWarning($"[FuzzyNotes] Could not open vault '{w.path}': {ex.Message}"); }
            }
        }

        private void StartWatching(NoteVault vault)
        {
            try
            {
                vault.Watcher = new NoteFileWatcher(vault.Root);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] File watcher unavailable for '{vault.Root}', external edits are detected on focus only: {ex.Message}");
            }
        }

        public void EnsureStorage()
        {
            try
            {
                Directory.CreateDirectory(RootDirectory);
                if (m_EditorServices) Directory.CreateDirectory(PrimaryVault.AssetsDirectory);

                // A friendly .gitignore inside the project vault that ignores only temporary files and the trash
                string gitignorePath = Path.Combine(RootDirectory, ".gitignore");
                if (!File.Exists(gitignorePath))
                {
                    File.WriteAllText(gitignorePath, "# Fuzzy Notes - Ignore temporary/swap files and trash\n*.swp\n*.tmp\n~*\nrecovery.json\n.trash/\n");
                }
                else
                {
                    string existing = File.ReadAllText(gitignorePath);
                    if (!existing.Split('\n').Any(l => l.Trim() == ".trash/"))
                    {
                        File.AppendAllText(gitignorePath, (existing.EndsWith("\n") ? "" : "\n") + ".trash/\n");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] Failed to initialize storage directories: {ex.Message}");
            }
        }

        // =====================================================================
        // Loading & lookup
        // =====================================================================

        /// <summary>Loads the vaults once; IDE integrations call this before reading the index.</summary>
        public void EnsureLoaded()
        {
            if (!m_Loaded) LoadAllNotes();
        }

        public void LoadAllNotes()
        {
            FlushPendingSaves();
            EnsureStorage();
            m_Loaded = true;

            string activePath = m_ActiveNote?.FilePath;
            var externalNotes = m_Notes.Where(n => n.IsExternal && File.Exists(n.FilePath)).ToList();
            m_Notes.Clear();

            foreach (var vault in m_Vaults)
            {
                try
                {
                    foreach (var file in EnumerateMarkdownFiles(vault))
                    {
                        if (!IsVaultFile(file)) continue;
                        var note = LoadNoteFile(file);
                        if (note != null) m_Notes.Add(note);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FuzzyNotes] Error loading notes from '{vault.Root}': {ex.Message}");
                }
            }

            m_Notes.AddRange(externalNotes);

            // Only the project vault counts: READMEs in Assets don't make a first-time vault "set up".
            if (m_EditorServices && !m_Notes.Any(n => n.VaultId == PrimaryVault.Id))
            {
                CreateDefaultWelcomeNote();
            }

            m_Index.Rebuild(m_Notes);

            m_ActiveNote = FindByPath(activePath)
                           ?? m_Notes.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.UpdatedAt).FirstOrDefault();

            RaiseNotesChanged();
            RaiseActiveNoteChanged();
        }

        /// <summary>Creates a note in <paramref name="folder"/> (vault-relative, "" = root) of <paramref name="vault"/> (default: project vault).</summary>
        public NoteItem CreateNote(string title, string initialBody = "", List<string> tags = null, string folder = null, NoteVault vault = null)
        {
            EnsureStorage();
            vault = vault ?? PrimaryVault;
            if (vault.IsProjectFiles)
            {
                // New notes never become Unity assets; they go to the project vault instead.
                vault = PrimaryVault;
                folder = null;
            }

            string cleanTitle = string.IsNullOrWhiteSpace(title) ? "Untitled Note" : title.Trim();
            string dir = vault.FolderPath(folder);
            Directory.CreateDirectory(dir);
            string targetPath = GetUniquePath(Path.Combine(dir, SanitizeFileName(cleanTitle) + ".md"));

            var note = NoteItem.CreateNew(cleanTitle, initialBody, tags);
            note.FilePath = targetPath;
            note.VaultId = vault.Id;
            note.RelativePath = vault.RelativePathOf(targetPath);
            if (Path.GetFileNameWithoutExtension(targetPath) != SanitizeFileName(cleanTitle))
            {
                note.Title = Path.GetFileNameWithoutExtension(targetPath);
            }

            SaveNote(note, immediate: true);

            m_Notes.Insert(0, note);
            m_Index.Update(note);
            SetActiveNote(note);
            RaiseNotesChanged();

            return note;
        }

        public NoteItem OpenExternalFile(string absolutePath)
        {
            if (!File.Exists(absolutePath)) return null;

            var existing = FindByPath(absolutePath);
            if (existing != null)
            {
                SetActiveNote(existing);
                return existing;
            }

            bool insideVault = IsVaultFile(absolutePath);
            var note = insideVault ? LoadNoteFile(absolutePath) : NoteItem.FromFile(absolutePath, isExternal: true);
            if (note == null) return null;
            if (!insideVault) note.RelativePath = Path.GetFileName(absolutePath);

            m_Notes.Add(note);
            m_Index.Update(note);
            SetActiveNote(note);
            RaiseNotesChanged();
            return note;
        }

        public NoteItem FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full = NormalizePath(path);
            return m_Notes.FirstOrDefault(n => !string.IsNullOrEmpty(n.FilePath) &&
                                               string.Equals(NormalizePath(n.FilePath), full, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves the target of a [[wiki link]] like Obsidian: vault path, then file name, then title, alias and id.
        /// Heading/alias suffixes are ignored. Notes in the same vault as <paramref name="context"/> win;
        /// "vault:id/Path" addresses another vault explicitly.
        /// </summary>
        public NoteItem FindNoteByLink(string linkText, NoteItem context = null)
        {
            MarkdownScan.SplitLinkTarget(linkText, out string notePart, out _, out _);
            string key = MarkdownScan.NormalizeLinkTarget(notePart);
            if (key.Length == 0) return null;

            IEnumerable<NoteItem> scope = m_Notes;
            if (key.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            {
                string rest = key.Substring(6);
                int slash = rest.IndexOf('/');
                if (slash <= 0) return null;
                var vault = GetVault(rest.Substring(0, slash));
                if (vault == null) return null;
                key = rest.Substring(slash + 1);
                scope = m_Notes.Where(n => n.VaultId == vault.Id);
                return FindIn(scope.ToList(), key);
            }

            if (context?.VaultId != null)
            {
                var sameVault = FindIn(m_Notes.Where(n => n.VaultId == context.VaultId).ToList(), key);
                if (sameVault != null) return sameVault;
            }
            return FindIn(m_Notes, key);
        }

        private static NoteItem FindIn(IReadOnlyList<NoteItem> notes, string key)
        {
            bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            if (key.IndexOf('/') >= 0)
            {
                var byPath = notes.FirstOrDefault(n => !string.IsNullOrEmpty(n.RelativePath) && !n.IsExternal &&
                    (Eq(MarkdownScan.NormalizeLinkTarget(n.RelativePath), key) ||
                     MarkdownScan.NormalizeLinkTarget(n.RelativePath).EndsWith("/" + key, StringComparison.OrdinalIgnoreCase)));
                if (byPath != null) return byPath;
            }

            return notes.FirstOrDefault(n => Eq(n.LinkName, key))
                   ?? notes.FirstOrDefault(n => Eq(n.Title, key))
                   ?? notes.FirstOrDefault(n => n.Aliases.Any(a => Eq(a, key)))
                   ?? notes.FirstOrDefault(n => Eq(n.Id, key));
        }

        public void SetActiveNote(NoteItem note)
        {
            if (m_ActiveNote == note) return;

            // Save current note before switching
            if (m_ActiveNote != null && m_ActiveNote.IsDirty)
            {
                SaveNote(m_ActiveNote, immediate: true);
            }

            m_ActiveNote = note;
            TouchRecent(note);
            RaiseActiveNoteChanged();
        }

        private void TouchRecent(NoteItem note)
        {
            if (note == null || string.IsNullOrEmpty(note.FilePath)) return;
            m_Recent.RemoveAll(p => string.Equals(p, note.FilePath, StringComparison.OrdinalIgnoreCase));
            m_Recent.Insert(0, note.FilePath);
            if (m_Recent.Count > MaxRecent) m_Recent.RemoveRange(MaxRecent, m_Recent.Count - MaxRecent);
            if (m_EditorServices) SessionState.SetString(RecentSessionKey, string.Join("\n", m_Recent));
        }

        // =====================================================================
        // Saving
        // =====================================================================

        /// <summary>Marks the note dirty and schedules a debounced save.</summary>
        public void RequestSave(NoteItem note)
        {
            if (note == null) return;
            note.IsDirty = true;
            m_PendingSaves.Add(note);
            if (m_EditorServices) m_LastEditTime = EditorApplication.timeSinceStartup; // only the autosave loop reads it
        }

        public void FlushPendingSaves()
        {
            if (m_PendingSaves.Count == 0) return;
            foreach (var note in m_PendingSaves.ToList())
            {
                if (note.IsDirty) SaveNote(note, immediate: true);
            }
            m_PendingSaves.Clear();
        }

        /// <summary>
        /// Writes the note if its serialized content differs from what is on disk. Refuses to overwrite
        /// a file that was changed externally since we last read it; that raises <see cref="OnNoteConflict"/> instead.
        /// </summary>
        /// <returns>True if the note is now in sync with disk.</returns>
        public bool SaveNote(NoteItem note, bool immediate = false)
        {
            if (note == null || string.IsNullOrEmpty(note.FilePath)) return false;
            m_PendingSaves.Remove(note);
            if (note.HasConflict) return false;

            try
            {
                string content = note.Serialize();
                bool exists = File.Exists(note.FilePath);

                if (exists && content == note.DiskContent)
                {
                    // Nothing changed (e.g. a no-op edit); do not touch the file or its timestamp.
                    note.RawContent = content;
                    note.IsDirty = false;
                    return true;
                }

                if (exists && (note.DiskContent == null || File.GetLastWriteTimeUtc(note.FilePath) != note.DiskWriteTimeUtc))
                {
                    string onDisk = NoteFileIO.ReadText(note.FilePath);
                    if (onDisk != note.DiskContent && onDisk != content)
                    {
                        RaiseConflict(note, onDisk);
                        return false;
                    }
                }

                var now = DateTime.UtcNow;
                content = note.Serialize(now);
                NoteFileIO.WriteAtomic(note.FilePath, content, note.HasBom);

                note.RawContent = content;
                note.DiskContent = content;
                note.DiskWriteTimeUtc = File.GetLastWriteTimeUtc(note.FilePath);
                note.UpdatedAt = now;
                note.IsDirty = false;

                if (m_Notes.Contains(note)) m_Index.Update(note);
                OnNoteSaved?.Invoke(note);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FuzzyNotes] Failed to save note '{note.FilePath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Appends markdown to a note from outside the editor (bridges, quick actions) and saves it.
        /// Raises <see cref="OnNoteReloaded"/> so an open editor picks up the change instead of overwriting it.
        /// </summary>
        public void AppendToNote(NoteItem note, string markdown)
        {
            if (note == null) return;
            note.AppendToBody(markdown);
            SaveNote(note, immediate: true);
            OnNoteReloaded?.Invoke(note);
        }

        /// <summary>Changes a task checkbox from outside the editor (e.g. the Kanban board) and saves the note.</summary>
        public bool SetTaskStatus(NoteItem note, int lineIndex, TaskStatus status)
        {
            if (note == null || !note.UpdateTaskStatus(lineIndex, status)) return false;
            SaveNote(note, immediate: true);
            OnNoteReloaded?.Invoke(note);
            return true;
        }

        /// <summary>
        /// Copies a file (e.g. a dropped screenshot) into the assets folder of <paramref name="context"/>'s vault
        /// (default: project vault). Returns the vault-relative path to embed ("assets/shot.png"), or null on failure.
        /// </summary>
        public string ImportAttachment(string sourcePath, NoteItem context = null)
        {
            try
            {
                var vault = VaultOf(context) ?? PrimaryVault;
                if (vault.IsProjectFiles)
                {
                    // Next to the Assets markdown file, like a README's images on GitHub.
                    string dir = Path.GetDirectoryName(context.FilePath);
                    string copy = GetUniquePath(Path.Combine(dir, Path.GetFileName(sourcePath)));
                    File.Copy(sourcePath, copy);
                    if (m_EditorServices) AssetDatabase.ImportAsset(ToAssetPath(copy));
                    return Path.GetFileName(copy);
                }
                Directory.CreateDirectory(vault.AssetsDirectory);
                string target = GetUniquePath(Path.Combine(vault.AssetsDirectory, Path.GetFileName(sourcePath)));
                File.Copy(sourcePath, target);
                return "assets/" + Path.GetFileName(target);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FuzzyNotes] Failed to import attachment '{sourcePath}': {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        // Trash
        // =====================================================================

        /// <summary>Moves the note to its vault's .trash folder (external files are only closed).</summary>
        public bool DeleteNote(NoteItem note)
        {
            if (note == null) return false;
            m_PendingSaves.Remove(note);

            var vault = VaultOf(note);
            if (!note.IsExternal && vault != null && vault.IsProjectFiles)
            {
                // Through the AssetDatabase so the .meta file goes too; the OS trash keeps it recoverable.
                if (!m_EditorServices || !AssetDatabase.MoveAssetToTrash(ToAssetPath(note.FilePath)))
                {
                    Debug.LogError($"[FuzzyNotes] Failed to move '{note.FilePath}' to the trash.");
                    return false;
                }
            }
            else if (!note.IsExternal && vault != null && !string.IsNullOrEmpty(note.FilePath))
            {
                try
                {
                    string trashPath = GetUniquePath(Path.Combine(vault.TrashDirectory, vault.RelativePathOf(note.FilePath)));
                    Directory.CreateDirectory(Path.GetDirectoryName(trashPath));

                    if (note.IsDirty || !File.Exists(note.FilePath))
                    {
                        // Keep unsaved edits in the trashed copy.
                        NoteFileIO.WriteAtomic(trashPath, note.Serialize(), note.HasBom);
                        if (File.Exists(note.FilePath)) File.Delete(note.FilePath);
                    }
                    else
                    {
                        File.Move(note.FilePath, trashPath);
                    }

                    m_Deleted.Push(new DeletedEntry { TrashPath = trashPath, OriginalPath = note.FilePath });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FuzzyNotes] Failed to move note '{note.FilePath}' to trash: {ex.Message}");
                    return false;
                }
            }

            RemoveFromList(note);
            RaiseNotesChanged();
            return true;
        }

        /// <summary>Restores the most recently trashed note or folder.</summary>
        public NoteItem RestoreLastDeleted()
        {
            while (m_Deleted.Count > 0)
            {
                var deleted = m_Deleted.Pop();
                try
                {
                    if (Directory.Exists(deleted.TrashPath))
                    {
                        string targetDir = GetUniqueDirectory(deleted.OriginalPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetDir));
                        Directory.Move(deleted.TrashPath, targetDir);
                        NoteItem first = null;
                        foreach (var file in Directory.GetFiles(targetDir, "*.md", SearchOption.AllDirectories))
                        {
                            if (!IsVaultFile(file)) continue;
                            var restoredNote = LoadNoteFile(file);
                            if (restoredNote == null) continue;
                            m_Notes.Add(restoredNote);
                            m_Index.Update(restoredNote);
                            first = first ?? restoredNote;
                        }
                        RaiseNotesChanged();
                        return first;
                    }

                    if (!File.Exists(deleted.TrashPath)) continue;

                    string target = GetUniquePath(deleted.OriginalPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(deleted.TrashPath, target);

                    var note = LoadNoteFile(target);
                    if (note == null) return null;
                    m_Notes.Add(note);
                    m_Index.Update(note);
                    SetActiveNote(note);
                    RaiseNotesChanged();
                    return note;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FuzzyNotes] Failed to restore '{deleted.TrashPath}': {ex.Message}");
                    return null;
                }
            }
            return null;
        }

        // =====================================================================
        // Rename & links
        // =====================================================================

        /// <summary>Number of [[links]] in other notes that point at <paramref name="note"/>.</summary>
        public int CountIncomingLinks(NoteItem note, out int noteCount)
        {
            noteCount = 0;
            if (note == null) return 0;

            var keys = GetOwnLinkKeys(note);
            int total = 0;
            foreach (var other in m_Notes)
            {
                if (other == note || !m_Index.GetOutgoingLinks(other).Any(keys.Contains)) continue;
                MarkdownScan.RewriteWikiLinks(other.Body, t => keys.Contains(t) ? t : null, out int c);
                if (c > 0)
                {
                    total += c;
                    noteCount++;
                }
            }
            return total;
        }

        /// <summary>
        /// Renames the note (and its file, for vault notes). With <paramref name="updateLinks"/> every
        /// [[link]] pointing at the old name is rewritten across all vaults.
        /// </summary>
        public bool RenameNote(NoteItem note, string newTitle, bool updateLinks = true)
        {
            if (note == null || string.IsNullOrWhiteSpace(newTitle) || note.HasConflict) return false;

            newTitle = newTitle.Trim();
            if (string.Equals(note.Title, newTitle, StringComparison.Ordinal)) return true;

            var oldKeys = GetOwnLinkKeys(note);
            string oldRelKey = string.IsNullOrEmpty(note.RelativePath) ? null : MarkdownScan.NormalizeLinkTarget(note.RelativePath);
            string oldPath = note.FilePath;

            try
            {
                if (!note.IsExternal && !string.IsNullOrEmpty(oldPath))
                {
                    string newPath = Path.Combine(Path.GetDirectoryName(oldPath), SanitizeFileName(newTitle) + ".md");
                    bool sameFile = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
                    if (!sameFile && File.Exists(newPath)) return false; // A note with that name already exists

                    if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
                    {
                        if (VaultOf(note)?.IsProjectFiles == true)
                        {
                            // Keeps the .meta file and the asset's GUID.
                            string error = m_EditorServices ? AssetDatabase.RenameAsset(ToAssetPath(oldPath), Path.GetFileNameWithoutExtension(newPath)) : "no AssetDatabase";
                            if (!string.IsNullOrEmpty(error))
                            {
                                Debug.LogError($"[FuzzyNotes] Failed to rename '{oldPath}': {error}");
                                return false;
                            }
                        }
                        else MoveFile(oldPath, newPath, sameFile);

                        // Notes without a front-matter id use their file name as id.
                        if (string.Equals(note.Id, Path.GetFileNameWithoutExtension(oldPath), StringComparison.Ordinal))
                            note.Id = Path.GetFileNameWithoutExtension(newPath);

                        note.FilePath = newPath;
                        note.RelativePath = VaultOf(note)?.RelativePathOf(newPath) ?? Path.GetFileName(newPath);
                    }
                }

                note.Title = newTitle;
                note.IsDirty = true;
                SaveNote(note, immediate: true);
                m_Index.Update(note);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FuzzyNotes] Failed to rename note: {ex.Message}");
                return false;
            }

            if (updateLinks)
            {
                string newName = note.IsExternal ? newTitle : note.LinkName;
                string newRelKey = string.IsNullOrEmpty(note.RelativePath) ? null : MarkdownScan.NormalizeLinkTarget(note.RelativePath);
                RewriteLinks(oldKeys, t =>
                {
                    if (!oldKeys.Contains(t)) return null;
                    return oldRelKey != null && newRelKey != null && string.Equals(t, oldRelKey, StringComparison.OrdinalIgnoreCase) && t.IndexOf('/') >= 0
                        ? newRelKey
                        : newName;
                });
            }

            RaiseNotesChanged();
            return true;
        }

        public void TogglePin(NoteItem note)
        {
            if (note == null) return;
            note.IsPinned = !note.IsPinned;
            note.IsDirty = true;
            SaveNote(note, immediate: true);
            RaiseNotesChanged();
        }

        // =====================================================================
        // External changes & conflicts
        // =====================================================================

        /// <summary>Re-checks every note against disk and picks up new files. Cheap: only stats unchanged files.</summary>
        public void CheckExternalChanges()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in m_Notes)
            {
                if (!string.IsNullOrEmpty(n.FilePath)) paths.Add(NormalizePath(n.FilePath));
            }

            foreach (var vault in m_Vaults)
            {
                try
                {
                    foreach (var file in EnumerateMarkdownFiles(vault))
                    {
                        if (IsVaultFile(file)) paths.Add(NormalizePath(file));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FuzzyNotes] Failed to scan '{vault.Root}': {ex.Message}");
                }
            }

            HandleExternalPaths(paths);
        }

        public void ResolveConflictKeepMine(NoteItem note)
        {
            if (note == null || !note.HasConflict) return;

            if (File.Exists(note.FilePath))
            {
                note.DiskContent = NoteFileIO.ReadText(note.FilePath);
                note.DiskWriteTimeUtc = File.GetLastWriteTimeUtc(note.FilePath);
            }
            else
            {
                note.DiskContent = null;
            }
            note.ConflictDiskContent = null;
            note.IsDirty = true;
            SaveNote(note, immediate: true);
        }

        public void ResolveConflictUseDisk(NoteItem note)
        {
            if (note == null || !note.HasConflict) return;

            if (File.Exists(note.FilePath))
            {
                string disk = NoteFileIO.ReadText(note.FilePath, out bool bom);
                note.HasBom = bom;
                note.ApplyDiskContent(disk, File.GetLastWriteTimeUtc(note.FilePath));
            }
            else
            {
                note.ApplyDiskContent(note.ConflictDiskContent, DateTime.MinValue);
            }

            m_PendingSaves.Remove(note);
            m_Index.Update(note);
            OnNoteReloaded?.Invoke(note);
        }

        /// <summary>Saves the local version as a "conflicted copy" next to the original, then loads the disk version.</summary>
        public NoteItem ResolveConflictKeepBoth(NoteItem note)
        {
            if (note == null || !note.HasConflict) return null;

            string dir = Path.GetDirectoryName(note.FilePath);
            string stem = Path.GetFileNameWithoutExtension(note.FilePath);
            string copyPath = GetUniquePath(Path.Combine(dir, $"{stem} (conflicted copy {DateTime.Now:yyyy-MM-dd HHmm}).md"));
            var vault = VaultOf(note);

            var copy = new NoteItem
            {
                FilePath = copyPath,
                VaultId = note.VaultId,
                RelativePath = vault?.RelativePathOf(copyPath) ?? Path.GetFileName(copyPath),
                LineEnding = note.LineEnding,
                HasBom = note.HasBom,
                CreatedAt = DateTime.UtcNow,
                RawContent = note.Serialize(),
            };
            copy.ParseContent();
            copy.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            copy.Title = Path.GetFileNameWithoutExtension(copyPath);
            copy.IsDirty = true;

            if (!SaveNote(copy, immediate: true)) return null;
            m_Notes.Add(copy);
            m_Index.Update(copy);

            ResolveConflictUseDisk(note);
            RaiseNotesChanged();
            return copy;
        }

        private void OnEditorUpdate()
        {
            if (m_PendingSaves.Count > 0 && EditorApplication.timeSinceStartup - m_LastEditTime >= AutoSaveDebounceSeconds)
            {
                FlushPendingSaves();
            }

            bool rescan = false;
            var changed = new List<string>();
            foreach (var vault in m_Vaults)
            {
                if (vault.Watcher == null || !vault.Watcher.Pump(out var paths, out bool fullRescan)) continue;
                if (fullRescan) rescan = true;
                else changed.AddRange(paths);
            }

            if (rescan) CheckExternalChanges();
            else if (changed.Count > 0) HandleExternalPaths(changed);
        }

        private void OnBeforeAssemblyReload()
        {
            FlushPendingSaves();
            foreach (var vault in m_Vaults)
            {
                vault.Watcher?.Dispose();
                vault.Watcher = null;
            }
        }

        private void HandleExternalPaths(IEnumerable<string> paths)
        {
            var deleted = new List<NoteItem>();
            var created = new List<string>();
            bool listChanged = false;

            foreach (var path in paths)
            {
                var note = FindByPath(path);
                if (File.Exists(path))
                {
                    if (note != null) HandleChangedOnDisk(note);
                    else if (IsVaultFile(path)) created.Add(path);
                }
                else if (note != null)
                {
                    deleted.Add(note);
                }
            }

            foreach (var path in created)
            {
                string content;
                try { content = NoteFileIO.ReadText(path); }
                catch (IOException) { continue; } // Still being written; the next event picks it up.

                // A delete + create with identical content is a move/rename done outside Fuzzy Notes.
                var moved = deleted.FirstOrDefault(d => !d.IsDirty && d.DiskContent == content);
                if (moved != null)
                {
                    deleted.Remove(moved);
                    var vault = VaultOfPath(path);
                    moved.FilePath = path;
                    moved.VaultId = vault?.Id;
                    moved.RelativePath = vault?.RelativePathOf(path) ?? Path.GetFileName(path);
                    moved.Title = Path.GetFileNameWithoutExtension(path);
                    moved.ApplyDiskContent(content, File.GetLastWriteTimeUtc(path));
                    m_Index.Update(moved);
                }
                else
                {
                    var note = LoadNoteFile(path);
                    if (note == null) continue;
                    m_Notes.Add(note);
                    m_Index.Update(note);
                }
                listChanged = true;
            }

            foreach (var note in deleted)
            {
                if (note.IsDirty)
                {
                    // Keep the in-memory edits; the next save recreates the file.
                    note.DiskContent = null;
                    Debug.LogWarning($"[FuzzyNotes] '{note.Title}' was deleted on disk but has unsaved edits; it will be recreated on save.");
                    continue;
                }
                RemoveFromList(note);
                listChanged = true;
            }

            if (listChanged) RaiseNotesChanged();
        }

        private void HandleChangedOnDisk(NoteItem note)
        {
            try
            {
                var writeTime = File.GetLastWriteTimeUtc(note.FilePath);
                if (writeTime == note.DiskWriteTimeUtc && !note.HasConflict) return;

                string content = NoteFileIO.ReadText(note.FilePath, out bool bom);
                if (content == note.DiskContent)
                {
                    note.DiskWriteTimeUtc = writeTime; // touched but not modified (or our own write)
                    return;
                }

                if (note.IsDirty || note.HasConflict)
                {
                    RaiseConflict(note, content);
                    return;
                }

                note.HasBom = bom;
                note.ApplyDiskContent(content, writeTime);
                m_Index.Update(note);
                OnNoteReloaded?.Invoke(note);
                RaiseNotesChanged();
            }
            catch (IOException)
            {
                // File locked by the writer; a later event or focus check will retry.
            }
        }

        // List/selection events drive the Notes window, which always shows Instance; secondary stores
        // (e.g. tests on a temp vault) must not push their notes into it.
        private bool IsPrimary => ReferenceEquals(this, s_Instance);

        private void RaiseNotesChanged()
        {
            if (IsPrimary) OnNotesChanged?.Invoke();
        }

        private void RaiseActiveNoteChanged()
        {
            if (IsPrimary) OnActiveNoteChanged?.Invoke(m_ActiveNote);
        }

        private static void RaiseReloaded(NoteItem note) => OnNoteReloaded?.Invoke(note);

        private void RaiseConflict(NoteItem note, string diskContent)
        {
            if (note.ConflictDiskContent == diskContent) return;
            note.ConflictDiskContent = diskContent;
            Debug.LogWarning($"[FuzzyNotes] '{note.Title}' changed on disk while it had unsaved edits. Resolve the conflict in Fuzzy Notes.");
            OnNoteConflict?.Invoke(note);
        }

        /// <summary>Link keys that currently resolve to <paramref name="note"/> (ambiguous names excluded).</summary>
        private HashSet<string> GetOwnLinkKeys(NoteItem note)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in NoteIndex.GetLinkKeys(note))
            {
                // Aliases and a front-matter id survive a rename, so links using them stay valid.
                if (note.Aliases.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                if (string.Equals(key, note.Id, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, note.LinkName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, note.Title, StringComparison.OrdinalIgnoreCase)) continue;
                if (FindNoteByLink(key) == note) keys.Add(key);
            }
            return keys;
        }

        private void RewriteLinks(HashSet<string> keys, Func<string, string> map)
        {
            foreach (var other in m_Notes.ToList())
            {
                if (other.HasConflict || !m_Index.GetOutgoingLinks(other).Any(keys.Contains)) continue;

                string newBody = MarkdownScan.RewriteWikiLinks(other.Body, map, out int count);
                if (count == 0) continue;

                other.SetBody(newBody);
                other.IsDirty = true;
                SaveNote(other, immediate: true);
                OnNoteReloaded?.Invoke(other);
            }
        }

        private void RemoveFromList(NoteItem note)
        {
            m_PendingSaves.Remove(note);
            m_Notes.Remove(note);
            m_Index.Remove(note);

            if (m_ActiveNote == note)
            {
                m_ActiveNote = m_Notes.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.UpdatedAt).FirstOrDefault();
                RaiseActiveNoteChanged();
            }
        }

        private NoteItem LoadNoteFile(string file)
        {
            try
            {
                var note = NoteItem.FromFile(file, isExternal: false);
                if (note != null)
                {
                    var vault = VaultOfPath(file);
                    note.VaultId = vault?.Id;
                    note.RelativePath = vault?.RelativePathOf(file) ?? Path.GetFileName(file);
                }
                return note;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] Failed to parse note file '{file}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// True for .md files inside a vault that are not under a dot-folder (.trash, .obsidian, ...); in the Assets
        /// vault also not excluded by <see cref="NoteSettings.ProjectMarkdownExcludes"/>.
        /// </summary>
        private bool IsVaultFile(string path)
        {
            var vault = VaultOfPath(path);
            if (vault == null || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
            if (vault.IsProjectFiles) return ProjectMarkdownFilter.IsIncluded(vault.RelativePathOf(path), m_ProjectExcludes);
            return !IsHiddenRelative(vault.RelativePathOf(path), includesFileName: true);
        }

        /// <summary>Candidate .md files of a vault; the Assets vault asks the AssetDatabase instead of walking the disk.</summary>
        private static IEnumerable<string> EnumerateMarkdownFiles(NoteVault vault)
        {
            if (vault.IsProjectFiles)
            {
                return AssetDatabase.GetAllAssetPaths()
                    .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFullPath)
                    .ToList();
            }
            return Directory.Exists(vault.Root) ? Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories) : Array.Empty<string>();
        }

        /// <summary>"Assets/Docs/Readme.md" for a file inside the project's Assets folder.</summary>
        private static string ToAssetPath(string fullPath)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(fullPath).Substring(root.Length + 1).Replace('\\', '/');
        }

        /// <summary>Picks up imported, deleted and moved .md assets (fed by an AssetPostprocessor).</summary>
        public void NotifyProjectFilesChanged(IEnumerable<string> assetPaths)
        {
            if (!m_Loaded || GetVault(NoteVault.ProjectFilesId) == null) return;
            var paths = assetPaths.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath).ToList();
            if (paths.Count > 0) HandleExternalPaths(paths);
        }

        /// <summary>True when any folder segment of a vault-relative path starts with '.'.</summary>
        private static bool IsHiddenRelative(string relative, bool includesFileName)
        {
            var segments = relative.Split('/');
            int folders = includesFileName ? segments.Length - 1 : segments.Length;
            for (int i = 0; i < folders; i++)
            {
                if (segments[i].StartsWith(".")) return true;
            }
            return false;
        }

        private static void MoveFile(string from, string to, bool caseOnly)
        {
            if (!File.Exists(from)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (caseOnly)
            {
                // Case-only rename: go through a temporary name so case-insensitive file systems accept it.
                string temp = from + ".renaming";
                File.Move(from, temp);
                File.Move(temp, to);
            }
            else File.Move(from, to);
        }

        private void CreateDefaultWelcomeNote()
        {
            string body = @"# Welcome to Fuzzy Notes! 📝

Plain Markdown files in `.fuzzynotes/`, edited in place with Live Preview — compatible with Obsidian.

> [!tip] Quick start
> Type `/` for blocks, `[[` to link a note or script, `#` for tags. Hover a link to preview it, Ctrl+Click to open it.
> Ctrl+O jumps to any note, Ctrl+P runs any command.

## 🎮 Unity & IDE
- **Code links**: [[PlayerMotor.cs:25]] or [[PlayerMotor.cs#Awake]] jump straight into Fuzzy IDE.
- **Asset links**: [[asset:Assets/Scenes/SampleScene.unity]] pings the asset. Drop images or assets onto the window.
- **Tasks**: checkboxes are tracked project-wide — open the **Tasks** board. Ctrl+Enter toggles a task.

## 📌 Try it
- [ ] Create a note with `Ctrl+Alt+N`
- [ ] Open today's daily note from the 📅 button
- [ ] Fold this section with the ▾ arrow next to the heading
- [ ] Search with `tag:guide` or `task:todo` in the sidebar

| Shortcut | Action |
| --- | --- |
| Ctrl+O / Ctrl+P | Quick switcher / command palette |
| Ctrl+B / Ctrl+I | Bold / italic |
| Alt+↑ / Alt+↓ | Move line |

## 💻 Run in the terminal
```sh
echo ""Hello from Fuzzy Notes!""
```

Happy Game Developing! 🚀
";
            CreateNote("Welcome to Fuzzy Notes", body, new List<string> { "guide", "welcome" });
        }

        private static string GetUniquePath(string path)
        {
            if (!File.Exists(path)) return path;

            string dir = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        private static string GetUniqueDirectory(string path)
        {
            if (!Directory.Exists(path)) return path;
            for (int i = 1; ; i++)
            {
                string candidate = $"{path} ({i})";
                if (!Directory.Exists(candidate)) return candidate;
            }
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        public static string SanitizeFileName(string name)
        {
            var invalids = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (Array.IndexOf(invalids, c) < 0) sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString().Trim();
        }
    }
}
