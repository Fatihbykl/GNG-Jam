using System;
using System.Collections.Generic;
using System.Linq;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>Everything the index derived from a single note; kept so the note can be removed cheaply.</summary>
    public sealed class NoteIndexEntry
    {
        public string Path;
        public List<string> Tags;
        /// <summary>Normalized link targets (see <see cref="MarkdownScan.NormalizeLinkTarget"/>), asset links excluded.</summary>
        public List<string> OutgoingLinks;
        public List<TaskItem> Tasks;
        /// <summary>Code / asset / scene-object / commit links (see <see cref="NoteRefLinks"/>).</summary>
        public List<NoteRefLink> Refs;
    }

    /// <summary>A note that references a code file, asset, scene object or commit.</summary>
    public readonly struct NoteRefHit
    {
        public readonly NoteItem Note;
        public readonly NoteRefLink Link;

        public NoteRefHit(NoteItem note, NoteRefLink link)
        {
            Note = note;
            Link = link;
        }
    }

    /// <summary>
    /// Incremental index over notes. <see cref="Update"/> and <see cref="Remove"/> touch only one note,
    /// so saving a note costs O(size of that note) instead of re-scanning the whole vault.
    /// </summary>
    public class NoteIndex
    {
        private readonly Dictionary<NoteItem, NoteIndexEntry> m_Entries = new Dictionary<NoteItem, NoteIndexEntry>();
        private readonly List<TaskItem> m_AllTasks = new List<TaskItem>();
        private bool m_TasksDirty;

        // Map of Tag -> Count
        public Dictionary<string, int> TagCounts { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Map of normalized link target -> referencing note file paths
        public Dictionary<string, HashSet<string>> Backlinks { get; } = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Map of NoteRefLink.Key -> links pointing at that code file / asset / object
        private readonly Dictionary<string, List<NoteRefHit>> m_RefsByKey = new Dictionary<string, List<NoteRefHit>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<NoteRefHit> s_NoRefs = new List<NoteRefHit>();

        /// <summary>Incremented on every change so callers can cache derived data (gutter markers, badges).</summary>
        public int Version { get; private set; }

        public IReadOnlyList<TaskItem> AllTasks
        {
            get
            {
                if (m_TasksDirty)
                {
                    m_AllTasks.Clear();
                    foreach (var e in m_Entries.Values) m_AllTasks.AddRange(e.Tasks);
                    m_TasksDirty = false;
                }
                return m_AllTasks;
            }
        }

        public void Rebuild(IEnumerable<NoteItem> notes)
        {
            m_Entries.Clear();
            TagCounts.Clear();
            Backlinks.Clear();
            m_RefsByKey.Clear();
            m_TasksDirty = true;
            Version++;

            if (notes == null) return;
            foreach (var note in notes) Update(note);
        }

        public void Update(NoteItem note)
        {
            if (note == null) return;
            Remove(note);

            var entry = new NoteIndexEntry
            {
                Path = note.FilePath,
                Tags = note.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                OutgoingLinks = MarkdownScan.ExtractWikiLinks(note.Body)
                    .Where(l => !l.StartsWith("asset:", StringComparison.OrdinalIgnoreCase))
                    .Select(MarkdownScan.NormalizeLinkTarget)
                    .Where(l => l.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Tasks = note.ExtractTasks(),
                Refs = NoteRefLinks.Extract(note.Body)
            };
            m_Entries[note] = entry;

            foreach (var tag in entry.Tags)
                TagCounts[tag] = TagCounts.TryGetValue(tag, out int c) ? c + 1 : 1;

            if (!string.IsNullOrEmpty(entry.Path))
            {
                foreach (var target in entry.OutgoingLinks)
                {
                    if (!Backlinks.TryGetValue(target, out var refs))
                    {
                        refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        Backlinks[target] = refs;
                    }
                    refs.Add(entry.Path);
                }
            }

            foreach (var link in entry.Refs)
            {
                string key = link.Key;
                if (!m_RefsByKey.TryGetValue(key, out var hits))
                {
                    hits = new List<NoteRefHit>();
                    m_RefsByKey[key] = hits;
                }
                hits.Add(new NoteRefHit(note, link));
            }

            m_TasksDirty = true;
            Version++;
        }

        public void Remove(NoteItem note)
        {
            if (note == null || !m_Entries.TryGetValue(note, out var entry)) return;
            m_Entries.Remove(note);

            foreach (var tag in entry.Tags)
            {
                if (TagCounts.TryGetValue(tag, out int c))
                {
                    if (c <= 1) TagCounts.Remove(tag);
                    else TagCounts[tag] = c - 1;
                }
            }

            if (!string.IsNullOrEmpty(entry.Path))
            {
                foreach (var target in entry.OutgoingLinks)
                {
                    if (Backlinks.TryGetValue(target, out var refs))
                    {
                        refs.Remove(entry.Path);
                        if (refs.Count == 0) Backlinks.Remove(target);
                    }
                }
            }

            foreach (var link in entry.Refs)
            {
                string key = link.Key;
                if (m_RefsByKey.TryGetValue(key, out var hits))
                {
                    hits.RemoveAll(h => h.Note == note);
                    if (hits.Count == 0) m_RefsByKey.Remove(key);
                }
            }

            m_TasksDirty = true;
            Version++;
        }

        /// <summary>Links whose <see cref="NoteRefLink.Key"/> is <paramref name="key"/> (see the NoteRefLinks.*Key helpers).</summary>
        public IReadOnlyList<NoteRefHit> GetRefs(string key) =>
            key != null && m_RefsByKey.TryGetValue(key, out var hits) ? hits : s_NoRefs;

        /// <summary>
        /// Code links that can point at <paramref name="assetPath"/> ("Assets/Scripts/PlayerMotor.cs"):
        /// bare file names match any folder, links with folders must match the path's tail.
        /// </summary>
        public List<NoteRefHit> GetCodeRefs(string assetPath)
        {
            var result = new List<NoteRefHit>();
            if (string.IsNullOrEmpty(assetPath)) return result;
            foreach (var hit in GetRefs(NoteRefLinks.CodeKey(assetPath)))
            {
                if (NoteRefLinks.CodeTargetMatches(hit.Link.Target, assetPath)) result.Add(hit);
            }
            return result;
        }

        public IReadOnlyList<NoteRefLink> GetRefLinks(NoteItem note) =>
            note != null && m_Entries.TryGetValue(note, out var e) ? e.Refs : (IReadOnlyList<NoteRefLink>)Array.Empty<NoteRefLink>();

        public IReadOnlyList<string> GetOutgoingLinks(NoteItem note) =>
            note != null && m_Entries.TryGetValue(note, out var e) ? e.OutgoingLinks : (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>Every name another note may use to link to <paramref name="note"/>.</summary>
        public static IEnumerable<string> GetLinkKeys(NoteItem note)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(note.LinkName)) keys.Add(note.LinkName);
            if (!string.IsNullOrEmpty(note.RelativePath)) keys.Add(MarkdownScan.NormalizeLinkTarget(note.RelativePath));
            if (!string.IsNullOrEmpty(note.Title)) keys.Add(note.Title);
            if (!string.IsNullOrEmpty(note.Id)) keys.Add(note.Id);
            foreach (var a in note.Aliases) keys.Add(a);
            return keys;
        }

        /// <summary>File paths of notes that link to <paramref name="note"/> (excluding itself).</summary>
        public HashSet<string> GetBacklinkPaths(NoteItem note)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (note == null) return result;

            foreach (var key in GetLinkKeys(note))
            {
                if (Backlinks.TryGetValue(key, out var refs)) result.UnionWith(refs);
            }
            if (!string.IsNullOrEmpty(note.FilePath)) result.Remove(note.FilePath);
            return result;
        }

        public List<NoteItem> Search(IEnumerable<NoteItem> notes, string query, string activeTagFilter = null)
        {
            // Operators (tag:, path:, task:todo, "phrase", -exclude...) are handled by NoteSearch.
            return NoteSearch.Run(notes, NoteSearchQuery.Parse(query), activeTagFilter).Select(r => r.Note).ToList();
        }
    }
}
