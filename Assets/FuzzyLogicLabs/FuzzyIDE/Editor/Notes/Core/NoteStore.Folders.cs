using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>Folders, moving notes, daily notes, templates and unlinked mentions.</summary>
    public partial class NoteStore
    {
        // =====================================================================
        // Folders
        // =====================================================================

        /// <summary>
        /// Vault-relative folders (forward slashes, sorted), excluding dot-folders and the root "assets" attachment folder.
        /// </summary>
        public List<string> GetFolders(NoteVault vault)
        {
            var result = new List<string>();
            // The Assets vault shows only the folders that hold its files (the tree adds those itself).
            if (vault == null || vault.IsProjectFiles || !Directory.Exists(vault.Root)) return result;

            try
            {
                foreach (var dir in Directory.GetDirectories(vault.Root, "*", SearchOption.AllDirectories))
                {
                    string rel = vault.RelativePathOf(dir);
                    if (IsHiddenRelative(rel, includesFileName: false)) continue;
                    if (rel.Equals("assets", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(rel);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] Failed to list folders of '{vault.Root}': {ex.Message}");
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public bool CreateFolder(NoteVault vault, string relativeFolder)
        {
            if (vault == null || vault.IsProjectFiles || string.IsNullOrWhiteSpace(relativeFolder)) return false;
            string clean = string.Join("/", relativeFolder.Replace('\\', '/').Split('/').Select(SanitizeFileName).Where(s => s.Length > 0));
            if (clean.Length == 0 || IsHiddenRelative(clean, includesFileName: false)) return false;

            Directory.CreateDirectory(vault.FolderPath(clean));
            RaiseNotesChanged();
            return true;
        }

        /// <summary>Renames the last segment of a folder; notes inside move along and path-style links are updated.</summary>
        public bool RenameFolder(NoteVault vault, string relativeFolder, string newName, bool updateLinks = true)
        {
            if (vault == null || string.IsNullOrEmpty(relativeFolder) || string.IsNullOrWhiteSpace(newName)) return false;
            newName = SanitizeFileName(newName.Trim());
            if (newName.Length == 0 || newName.StartsWith(".")) return false;

            int slash = relativeFolder.LastIndexOf('/');
            string parent = slash < 0 ? "" : relativeFolder.Substring(0, slash);
            string newRelative = parent.Length == 0 ? newName : parent + "/" + newName;
            return MoveFolder(vault, relativeFolder, newRelative, updateLinks);
        }

        /// <summary>Moves a folder within its vault (e.g. drag &amp; drop onto another folder).</summary>
        public bool MoveFolder(NoteVault vault, string relativeFolder, string newRelativeFolder, bool updateLinks = true)
        {
            // Assets folders are Unity's to manage (the Project window keeps .meta files and references intact).
            if (vault == null || vault.IsProjectFiles || string.IsNullOrEmpty(relativeFolder) || string.IsNullOrEmpty(newRelativeFolder)) return false;
            if (string.Equals(relativeFolder, newRelativeFolder, StringComparison.Ordinal)) return true;
            if (newRelativeFolder.StartsWith(relativeFolder + "/", StringComparison.OrdinalIgnoreCase)) return false; // into itself

            string from = vault.FolderPath(relativeFolder);
            string to = vault.FolderPath(newRelativeFolder);
            bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if (!Directory.Exists(from) || (!caseOnly && Directory.Exists(to))) return false;

            FlushPendingSaves();
            var affected = m_Notes.Where(n => n.VaultId == vault.Id && IsUnder(n.RelativePath, relativeFolder)).ToList();
            var oldKeys = affected.ToDictionary(n => n, n => MarkdownScan.NormalizeLinkTarget(n.RelativePath));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                if (caseOnly)
                {
                    string temp = from + ".renaming";
                    Directory.Move(from, temp);
                    Directory.Move(temp, to);
                }
                else Directory.Move(from, to);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FuzzyNotes] Failed to move folder '{relativeFolder}': {ex.Message}");
                return false;
            }

            foreach (var note in affected)
            {
                string rest = note.RelativePath.Substring(relativeFolder.Length);
                note.RelativePath = newRelativeFolder + rest;
                note.FilePath = Path.Combine(vault.Root, note.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                m_Index.Update(note);
            }

            if (updateLinks) RewritePathLinks(affected.ToDictionary(n => oldKeys[n], n => MarkdownScan.NormalizeLinkTarget(n.RelativePath), StringComparer.OrdinalIgnoreCase));
            RaiseNotesChanged();
            return true;
        }

        /// <summary>Moves a folder (and its notes) to the vault's trash; <see cref="RestoreLastDeleted"/> brings it back.</summary>
        public bool DeleteFolder(NoteVault vault, string relativeFolder)
        {
            if (vault == null || vault.IsProjectFiles || string.IsNullOrEmpty(relativeFolder)) return false;
            string dir = vault.FolderPath(relativeFolder);
            if (!Directory.Exists(dir)) return false;

            FlushPendingSaves();
            try
            {
                string trashDir = GetUniqueDirectory(Path.Combine(vault.TrashDirectory, relativeFolder.Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(trashDir));
                Directory.Move(dir, trashDir);
                m_Deleted.Push(new DeletedEntry { TrashPath = trashDir, OriginalPath = dir });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FuzzyNotes] Failed to move folder '{relativeFolder}' to trash: {ex.Message}");
                return false;
            }

            foreach (var note in m_Notes.Where(n => n.VaultId == vault.Id && IsUnder(n.RelativePath, relativeFolder)).ToList())
                RemoveFromList(note);
            RaiseNotesChanged();
            return true;
        }

        /// <summary>
        /// Moves a note to a folder (possibly in another vault). Links written as paths ([[Folder/Note]]) are updated;
        /// name-only links keep working because the file name does not change.
        /// </summary>
        public bool MoveNote(NoteItem note, NoteVault vault, string relativeFolder, bool updateLinks = true)
        {
            if (note == null || note.IsExternal || note.HasConflict || vault == null) return false;
            if (vault.IsProjectFiles || VaultOf(note)?.IsProjectFiles == true) return false;

            string newPath = Path.Combine(vault.FolderPath(relativeFolder), Path.GetFileName(note.FilePath));
            if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(note.FilePath), StringComparison.OrdinalIgnoreCase)) return true;
            if (File.Exists(newPath)) return false;

            if (note.IsDirty) SaveNote(note, immediate: true);
            string oldKey = MarkdownScan.NormalizeLinkTarget(note.RelativePath ?? "");

            try
            {
                MoveFile(note.FilePath, newPath, caseOnly: false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FuzzyNotes] Failed to move note '{note.FilePath}': {ex.Message}");
                return false;
            }

            note.FilePath = newPath;
            note.VaultId = vault.Id;
            note.RelativePath = vault.RelativePathOf(newPath);
            m_Index.Update(note);

            if (updateLinks) RewritePathLinks(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { oldKey, MarkdownScan.NormalizeLinkTarget(note.RelativePath) } });
            RaiseNotesChanged();
            return true;
        }

        /// <summary>Rewrites links written as vault paths (containing '/') from old to new keys.</summary>
        private void RewritePathLinks(Dictionary<string, string> oldToNew)
        {
            var keys = new HashSet<string>(oldToNew.Keys.Where(k => k.IndexOf('/') >= 0), StringComparer.OrdinalIgnoreCase);
            if (keys.Count == 0) return;

            // A note moved to a vault root gets a plain name link, which is what its new key already is.
            RewriteLinks(keys, t => keys.Contains(t) ? oldToNew[t] : null);
        }

        private static bool IsUnder(string relativePath, string folder) =>
            !string.IsNullOrEmpty(relativePath) && relativePath.Replace('\\', '/').StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

        // =====================================================================
        // Daily notes & templates
        // =====================================================================

        public static string DailyNoteName(DateTime date, DailyNoteOptions options)
        {
            try { return SanitizeFileName(date.ToString(options.DateFormat)); }
            catch (FormatException) { return date.ToString("yyyy-MM-dd"); }
        }

        /// <summary>Opens (creating if needed) the daily note for <paramref name="date"/> in the project vault.</summary>
        public NoteItem OpenDailyNote(DateTime date, DailyNoteOptions options, TemplateContext context)
        {
            options = options ?? new DailyNoteOptions();
            string name = DailyNoteName(date, options);
            string folder = (options.Folder ?? "").Trim('/', '\\');
            string path = Path.Combine(PrimaryVault.FolderPath(folder), name + ".md");

            var existing = FindByPath(path);
            if (existing != null)
            {
                SetActiveNote(existing);
                return existing;
            }

            context = context ?? new TemplateContext();
            context.Now = date.Date + DateTime.Now.TimeOfDay;
            context.Title = name;

            string template = NoteTemplates.DefaultDailyTemplate;
            if (!string.IsNullOrWhiteSpace(options.Template))
            {
                var templateNote = FindNoteByLink(options.Template);
                if (templateNote != null) template = templateNote.Body;
            }

            return CreateNote(name, NoteTemplates.Apply(template, context), new List<string> { "daily" }, folder);
        }

        /// <summary>Notes inside the templates folder of the project vault.</summary>
        public List<NoteItem> GetTemplates(string templatesFolder)
        {
            string folder = (templatesFolder ?? "").Trim('/', '\\');
            if (folder.Length == 0) return new List<NoteItem>();
            return m_Notes.Where(n => n.VaultId == PrimaryVault.Id && IsUnder(n.RelativePath, folder))
                          .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        }

        // =====================================================================
        // Unlinked mentions
        // =====================================================================

        private static IEnumerable<string> MentionNames(NoteItem target) =>
            new[] { target.Title, target.LinkName }.Concat(target.Aliases).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Notes mentioning <paramref name="target"/> by name in plain text (not linked).</summary>
        public List<(NoteItem note, int count)> FindUnlinkedMentions(NoteItem target)
        {
            var result = new List<(NoteItem, int)>();
            if (target == null) return result;

            var names = MentionNames(target).ToList();
            foreach (var note in m_Notes)
            {
                if (note == target || string.IsNullOrEmpty(note.Body)) continue;
                if (!names.Any(n => note.Body.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) continue; // cheap pre-filter
                int count = MarkdownScan.CountUnlinkedMentions(note.Body, names);
                if (count > 0) result.Add((note, count));
            }
            return result.OrderByDescending(r => r.Item2).ToList();
        }

        /// <summary>Links every plain mention of <paramref name="target"/> inside <paramref name="source"/>.</summary>
        public int LinkMentions(NoteItem source, NoteItem target)
        {
            if (source == null || target == null || source.HasConflict) return 0;

            string body = MarkdownScan.LinkUnlinkedMentions(source.Body, MentionNames(target), target.LinkName, out int count);
            if (count == 0) return 0;

            source.SetBody(body);
            source.IsDirty = true;
            SaveNote(source, immediate: true);
            RaiseReloaded(source);
            RaiseNotesChanged();
            return count;
        }
    }
}
