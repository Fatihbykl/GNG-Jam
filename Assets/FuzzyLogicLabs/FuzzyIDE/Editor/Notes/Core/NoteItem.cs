using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    [Serializable]
    public class NoteItem
    {
        public string Id;
        public string Title;
        public string FilePath;
        /// <summary>Path relative to the note's vault root.</summary>
        public string RelativePath;
        /// <summary>Id of the <see cref="NoteVault"/> the file lives in (null for external files).</summary>
        public string VaultId;
        /// <summary>Current serialized note (front-matter + body). Task line indices refer to this text.</summary>
        public string RawContent;
        /// <summary>Markdown body without front-matter, always '\n' line endings.</summary>
        public string Body;
        /// <summary>Tags persisted in front-matter. This is the list to edit.</summary>
        public List<string> FrontMatterTags = new List<string>();
        /// <summary>#tags found in the body outside code. Derived; never written to front-matter.</summary>
        public List<string> InlineTags = new List<string>();
        public List<string> Aliases = new List<string>();
        public bool IsPinned;
        public DateTime CreatedAt;
        public DateTime UpdatedAt;
        public bool IsExternal;
        public bool IsDirty;

        /// <summary>Line ending used when writing ("\n" or "\r\n"), detected from the file.</summary>
        public string LineEnding = "\n";
        public bool HasBom;

        /// <summary>Exact text last read from or written to disk; null if the note was never on disk.</summary>
        [NonSerialized] public string DiskContent;
        [NonSerialized] public DateTime DiskWriteTimeUtc;
        /// <summary>Set when the file changed on disk while this note had unsaved edits.</summary>
        [NonSerialized] public string ConflictDiskContent;

        public bool HasConflict => ConflictDiskContent != null;

        /// <summary>Front-matter tags ∪ inline tags (lower-case).</summary>
        public IReadOnlyList<string> Tags => m_AllTags;

        /// <summary>Name other notes use to link here: the file name without extension (Obsidian semantics).</summary>
        public string LinkName => string.IsNullOrEmpty(FilePath) ? Title : Path.GetFileNameWithoutExtension(FilePath);

        private readonly List<string> m_AllTags = new List<string>();
        private FrontMatter m_FrontMatter;          // null = file has no front-matter block
        private List<string> m_ParsedFrontMatterTags = new List<string>();

        private static readonly Regex FrontMatterRegex =
            new Regex(@"^---[ \t]*\r?\n(?:([\s\S]*?)\r?\n)?---[ \t]*(?:\r?\n|$)", RegexOptions.Compiled);

        public static NoteItem CreateNew(string title, string initialBody = "", List<string> tags = null)
        {
            var now = DateTime.UtcNow;
            var note = new NoteItem
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Title = string.IsNullOrWhiteSpace(title) ? "Untitled Note" : title.Trim(),
                Body = MarkdownScan.NormalizeNewlines(initialBody),
                FrontMatterTags = tags != null ? tags.Select(NormalizeTag).Where(t => t.Length > 0).Distinct().ToList() : new List<string>(),
                CreatedAt = now,
                UpdatedAt = now,
                IsDirty = true
            };

            var fm = new FrontMatter();
            fm.SetScalar("id", note.Id);
            fm.SetScalar("title", note.Title, forceQuote: true);
            if (note.FrontMatterTags.Count > 0) fm.SetList("tags", note.FrontMatterTags);
            fm.SetScalar("created", FormatDate(now));
            fm.SetScalar("updated", FormatDate(now));
            note.m_FrontMatter = fm;

            note.RawContent = note.Serialize();
            note.ParseContent();
            return note;
        }

        public static NoteItem FromFile(string filePath, bool isExternal = false)
        {
            if (!File.Exists(filePath)) return null;

            string raw = NoteFileIO.ReadText(filePath, out bool hasBom);
            var fileInfo = new FileInfo(filePath);
            var note = new NoteItem
            {
                FilePath = filePath,
                IsExternal = isExternal,
                HasBom = hasBom,
                Id = Path.GetFileNameWithoutExtension(filePath),
                Title = Path.GetFileNameWithoutExtension(filePath),
                CreatedAt = fileInfo.CreationTimeUtc,
                UpdatedAt = fileInfo.LastWriteTimeUtc,
            };
            note.ApplyDiskContent(raw, fileInfo.LastWriteTimeUtc);
            return note;
        }

        /// <summary>Replaces in-memory state with text read from disk (clears dirty/conflict state).</summary>
        public void ApplyDiskContent(string raw, DateTime writeTimeUtc)
        {
            raw = raw ?? "";
            LineEnding = raw.Contains("\r\n") ? "\r\n" : "\n";
            DiskContent = raw;
            DiskWriteTimeUtc = writeTimeUtc;
            RawContent = raw;
            ConflictDiskContent = null;
            IsDirty = false;
            ParseContent();
        }

        /// <summary>Re-derives Body, metadata and tags from <see cref="RawContent"/>.</summary>
        public void ParseContent()
        {
            string raw = RawContent ?? "";
            var match = FrontMatterRegex.Match(raw);
            if (match.Success)
            {
                m_FrontMatter = FrontMatter.Parse(match.Groups[1].Value);
                Body = MarkdownScan.NormalizeNewlines(raw.Substring(match.Length));
            }
            else
            {
                m_FrontMatter = null;
                Body = MarkdownScan.NormalizeNewlines(raw);
            }

            ReadFrontMatterFields();
            RefreshDerivedTags();
        }

        private void ReadFrontMatterFields()
        {
            var fm = m_FrontMatter;
            m_ParsedFrontMatterTags = new List<string>();
            Aliases = new List<string>();
            IsPinned = false;

            if (fm != null)
            {
                string id = fm.GetScalar("id");
                if (!string.IsNullOrEmpty(id)) Id = id;

                string title = fm.GetScalar("title");
                if (!string.IsNullOrWhiteSpace(title)) Title = title;

                bool.TryParse(fm.GetScalar("pinned") ?? "", out IsPinned);

                if (TryParseDate(fm.GetScalar("created"), out var created)) CreatedAt = created;
                if (TryParseDate(fm.GetScalar("updated"), out var updated)) UpdatedAt = updated;

                foreach (var t in fm.GetList("tags", splitOnWhitespace: true).Concat(fm.GetList("tag", splitOnWhitespace: true)))
                {
                    string tag = NormalizeTag(t);
                    if (tag.Length > 0 && !m_ParsedFrontMatterTags.Contains(tag)) m_ParsedFrontMatterTags.Add(tag);
                }

                foreach (var a in fm.GetList("aliases").Concat(fm.GetList("alias")))
                {
                    if (!Aliases.Contains(a)) Aliases.Add(a);
                }
            }

            FrontMatterTags = new List<string>(m_ParsedFrontMatterTags);
        }

        private void RefreshDerivedTags()
        {
            InlineTags = MarkdownScan.ExtractInlineTags(Body);
            m_AllTags.Clear();
            foreach (var t in FrontMatterTags.Concat(InlineTags))
            {
                if (!m_AllTags.Contains(t)) m_AllTags.Add(t);
            }
        }

        /// <summary>Serializes without touching the <c>updated</c> timestamp.</summary>
        public string Serialize() => Serialize(null);

        /// <summary>
        /// Builds the file text. Only front-matter entries whose values changed are rewritten.
        /// <paramref name="updatedStamp"/> refreshes an existing <c>updated</c> key; it is never added.
        /// </summary>
        public string Serialize(DateTime? updatedStamp)
        {
            SyncFieldsToFrontMatter(updatedStamp);

            var sb = new StringBuilder();
            if (m_FrontMatter != null && !m_FrontMatter.IsEmpty)
            {
                sb.Append("---\n").Append(m_FrontMatter).Append('\n').Append("---\n");
            }
            sb.Append(MarkdownScan.NormalizeNewlines(Body));

            string text = sb.ToString();
            return LineEnding == "\n" ? text : text.Replace("\n", LineEnding);
        }

        private void SyncFieldsToFrontMatter(DateTime? updatedStamp)
        {
            if (m_FrontMatter == null)
            {
                if (!IsPinned && FrontMatterTags.Count == 0) return;
                m_FrontMatter = new FrontMatter();
            }
            var fm = m_FrontMatter;

            if (fm.Has("id") && !string.IsNullOrEmpty(Id) && fm.GetScalar("id") != Id)
                fm.SetScalar("id", Id);

            if (fm.Has("title") && !string.IsNullOrWhiteSpace(Title) && fm.GetScalar("title") != Title)
                fm.SetScalar("title", Title, forceQuote: true);

            if (!FrontMatterTags.SequenceEqual(m_ParsedFrontMatterTags))
            {
                fm.Remove("tag");
                if (FrontMatterTags.Count > 0 || fm.Has("tags")) fm.SetList("tags", FrontMatterTags);
                m_ParsedFrontMatterTags = new List<string>(FrontMatterTags);
            }

            bool storedPinned = bool.TryParse(fm.GetScalar("pinned") ?? "", out bool p) && p;
            if (IsPinned && !storedPinned) fm.SetScalar("pinned", "true");
            else if (!IsPinned && storedPinned) fm.Remove("pinned");

            if (updatedStamp.HasValue && fm.Has("updated"))
            {
                fm.SetScalar("updated", FormatDate(updatedStamp.Value));
                UpdatedAt = updatedStamp.Value;
            }
        }

        /// <summary>Appends markdown to the body and marks the note dirty.</summary>
        public void AppendToBody(string markdown)
        {
            Body = (Body ?? "") + MarkdownScan.NormalizeNewlines(markdown);
            RawContent = Serialize();
            ParseContent();
            IsDirty = true;
        }

        /// <summary>Replaces the body (e.g. from the editor) and re-derives tags.</summary>
        public void SetBody(string body)
        {
            Body = MarkdownScan.NormalizeNewlines(body);
            RawContent = Serialize();
            ParseContent();
        }

        // =====================================================================
        // Properties (front-matter as key/value pairs for the properties panel)
        // =====================================================================

        /// <summary>Keys managed by Fuzzy Notes itself; shown but not editable as plain properties.</summary>
        public static readonly string[] ReadOnlyProperties = { "id", "title", "created", "updated" };

        /// <summary>Folder part of <see cref="RelativePath"/> with forward slashes ("" for the vault root).</summary>
        public string Folder
        {
            get
            {
                if (string.IsNullOrEmpty(RelativePath)) return "";
                string rel = RelativePath.Replace('\\', '/');
                int slash = rel.LastIndexOf('/');
                return slash < 0 ? "" : rel.Substring(0, slash);
            }
        }

        public List<string> PropertyKeys => m_FrontMatter != null ? m_FrontMatter.Keys : new List<string>();

        public bool IsListProperty(string key) => m_FrontMatter != null && m_FrontMatter.IsList(key);

        public string GetPropertyText(string key)
        {
            if (m_FrontMatter == null) return "";
            return m_FrontMatter.IsList(key) ? string.Join(", ", m_FrontMatter.GetList(key)) : m_FrontMatter.GetScalar(key) ?? "";
        }

        /// <summary>Sets a front-matter property. Tags and pinned go through their fields; lists are comma separated.</summary>
        public void SetProperty(string key, string text, bool asList)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0 || IsReadOnlyProperty(key)) return;
            var items = (text ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            if (IsKey(key, "tags") || IsKey(key, "tag"))
            {
                FrontMatterTags = items.Select(NormalizeTag).Where(t => t.Length > 0).Distinct().ToList();
            }
            else if (IsKey(key, "pinned"))
            {
                bool.TryParse((text ?? "").Trim(), out IsPinned);
            }
            else
            {
                if (m_FrontMatter == null) m_FrontMatter = new FrontMatter();
                if (asList) m_FrontMatter.SetList(key, items);
                else m_FrontMatter.SetScalar(key, (text ?? "").Trim());
            }

            RawContent = Serialize();
            ParseContent();
            IsDirty = true;
        }

        public void RemoveProperty(string key)
        {
            if (m_FrontMatter == null || IsReadOnlyProperty(key)) return;

            if (IsKey(key, "tags") || IsKey(key, "tag"))
            {
                m_FrontMatter.Remove("tags");
                m_FrontMatter.Remove("tag");
                FrontMatterTags = new List<string>();
                m_ParsedFrontMatterTags = new List<string>();
            }
            else if (IsKey(key, "pinned"))
            {
                IsPinned = false;
            }
            else
            {
                m_FrontMatter.Remove(key);
            }

            RawContent = Serialize();
            ParseContent();
            IsDirty = true;
        }

        public static bool IsReadOnlyProperty(string key) => ReadOnlyProperties.Any(k => IsKey(key, k));

        private static bool IsKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public List<TaskItem> ExtractTasks()
        {
            var results = new List<TaskItem>();
            if (string.IsNullOrEmpty(RawContent)) return results;

            MarkdownScan.ForEachProseLine(RawContent, (line, i) =>
            {
                if (TaskItem.TryParse(line, i, FilePath, Title, out var task)) results.Add(task);
            });
            return results;
        }

        public bool UpdateTaskStatus(int lineIndex, TaskStatus newStatus)
        {
            if (string.IsNullOrEmpty(RawContent)) return false;

            var lines = MarkdownScan.NormalizeNewlines(RawContent).Split('\n');
            if (lineIndex < 0 || lineIndex >= lines.Length) return false;

            string line = lines[lineIndex];
            if (!TaskItem.TryParse(line, lineIndex, FilePath, Title, out var task)) return false;

            task.Status = newStatus;

            int indentLen = 0;
            while (indentLen < line.Length && (line[indentLen] == ' ' || line[indentLen] == '\t'))
                indentLen++;

            lines[lineIndex] = task.ToMarkdownLine(line.Substring(0, indentLen));
            RawContent = string.Join("\n", lines);
            ParseContent();
            IsDirty = true;
            return true;
        }

        public static string NormalizeTag(string tag) => (tag ?? "").Trim().TrimStart('#').ToLowerInvariant();

        private static string FormatDate(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

        private static bool TryParseDate(string value, out DateTime result) =>
            DateTime.TryParse(value ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out result);
    }

    /// <summary>File helpers that keep encoding (BOM) intact and write atomically.</summary>
    public static class NoteFileIO
    {
        public static string ReadText(string path, out bool hasBom)
        {
            byte[] bytes = File.ReadAllBytes(path);
            hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int offset = hasBom ? 3 : 0;
            return new UTF8Encoding(false).GetString(bytes, offset, bytes.Length - offset);
        }

        public static string ReadText(string path) => ReadText(path, out _);

        /// <summary>Writes via a temp file + replace so a crash never leaves a half-written note.</summary>
        public static void WriteAtomic(string path, string content, bool withBom)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string tempFile = Path.Combine(dir ?? "", $".{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempFile, content, new UTF8Encoding(withBom));
                if (!File.Exists(path))
                {
                    File.Move(tempFile, path);
                    return;
                }

                try
                {
                    File.Replace(tempFile, path, null, true);
                }
                catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
                {
                    // Some file systems (network shares, FAT) do not support ReplaceFile.
                    File.Copy(tempFile, path, true);
                }
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
