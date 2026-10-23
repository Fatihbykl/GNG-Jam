using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// Parsed search query. Supported syntax (space separated, AND-combined):
    /// <c>word</c>, <c>"exact phrase"</c>, <c>-excluded</c>, <c>tag:x</c> / <c>#x</c>, <c>-tag:x</c>,
    /// <c>path:folder</c>, <c>file:name</c>, <c>vault:id</c>, <c>task:todo|done|any</c> (also <c>has:todo</c>).
    /// </summary>
    public sealed class NoteSearchQuery
    {
        public readonly List<string> Terms = new List<string>();
        public readonly List<string> Phrases = new List<string>();
        public readonly List<string> Excluded = new List<string>();
        public readonly List<string> Tags = new List<string>();
        public readonly List<string> ExcludedTags = new List<string>();
        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Files = new List<string>();
        public string Vault;
        public string Task;
        /// <summary>Words, phrases and exclusions compare case-sensitively (operators never do).</summary>
        public bool MatchCase;

        internal StringComparison TextComparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        public bool IsEmpty => Terms.Count == 0 && Phrases.Count == 0 && Excluded.Count == 0 && Tags.Count == 0 &&
                               ExcludedTags.Count == 0 && Paths.Count == 0 && Files.Count == 0 && Vault == null && Task == null;

        /// <summary>Words and phrases whose occurrences should be highlighted in results.</summary>
        public IEnumerable<string> HighlightTerms => Phrases.Concat(Terms);

        public static NoteSearchQuery Parse(string text)
        {
            var q = new NoteSearchQuery();
            foreach (var token in Tokenize(text ?? ""))
            {
                bool negate = token.Length > 1 && token[0] == '-';
                string t = negate ? token.Substring(1) : token;
                if (t.Length == 0) continue;

                if (t.Length >= 2 && t[0] == '"')
                {
                    string phrase = t.Trim('"');
                    if (phrase.Length == 0) continue;
                    (negate ? q.Excluded : q.Phrases).Add(phrase);
                    continue;
                }

                int colon = t.IndexOf(':');
                string op = colon > 0 ? t.Substring(0, colon).ToLowerInvariant() : null;
                string val = colon > 0 ? t.Substring(colon + 1).Trim('"') : null;

                if (t[0] == '#' && t.Length > 1) { op = "tag"; val = t.Substring(1); }

                switch (op)
                {
                    case "tag":
                        if (val.Length > 0) (negate ? q.ExcludedTags : q.Tags).Add(NoteItem.NormalizeTag(val));
                        break;
                    case "path":
                        if (val.Length > 0) q.Paths.Add(val.Replace('\\', '/'));
                        break;
                    case "file":
                        if (val.Length > 0) q.Files.Add(val);
                        break;
                    case "vault":
                        if (val.Length > 0) q.Vault = val;
                        break;
                    case "task":
                        q.Task = val.Length == 0 ? "any" : val.ToLowerInvariant();
                        break;
                    case "has":
                    case "is":
                        if (val.Equals("todo", StringComparison.OrdinalIgnoreCase)) q.Task = "todo";
                        else (negate ? q.Excluded : q.Terms).Add(t);
                        break;
                    default:
                        (negate ? q.Excluded : q.Terms).Add(t);
                        break;
                }
            }
            return q;
        }

        private static IEnumerable<string> Tokenize(string text)
        {
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char c in text)
            {
                if (c == '"') { quoted = !quoted; sb.Append(c); continue; }
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }

    /// <summary>One body line containing a searched word or phrase.</summary>
    public sealed class NoteSearchMatch
    {
        public int Line;
        public string Snippet;
        public int MatchStart;
        public int MatchLength;
    }

    public sealed class NoteSearchResult
    {
        public NoteItem Note;
        public int Score;
        /// <summary>Body line of the first text match, or -1.</summary>
        public int Line = -1;
        public string Snippet;
        public int SnippetMatchStart = -1;
        public int SnippetMatchLength;
        /// <summary>Every matching body line (up to <see cref="NoteSearch.MaxMatchesPerNote"/>), in document order.</summary>
        public readonly List<NoteSearchMatch> Matches = new List<NoteSearchMatch>();
        /// <summary>Matching lines beyond the ones kept in <see cref="Matches"/>.</summary>
        public int MoreMatches;
    }

    public static class NoteSearch
    {
        private const int SnippetRadius = 48;
        public const int MaxMatchesPerNote = 100;

        /// <summary>Filters and ranks notes. An empty query returns everything, pinned first, newest first.</summary>
        public static List<NoteSearchResult> Run(IEnumerable<NoteItem> notes, NoteSearchQuery query, string tagFilter = null)
        {
            var results = new List<NoteSearchResult>();
            if (notes == null) return results;

            foreach (var note in notes)
            {
                if (note == null) continue;
                if (!string.IsNullOrEmpty(tagFilter) && !HasTag(note, NoteItem.NormalizeTag(tagFilter))) continue;
                var r = Match(note, query);
                if (r != null) results.Add(r);
            }

            return results
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Note.IsPinned)
                .ThenByDescending(r => r.Note.UpdatedAt)
                .ToList();
        }

        public static NoteSearchResult Match(NoteItem note, NoteSearchQuery q)
        {
            var result = new NoteSearchResult { Note = note };
            if (q == null || q.IsEmpty) return result;

            string title = note.Title ?? "";
            string body = note.Body ?? "";
            string path = (note.RelativePath ?? note.FilePath ?? "").Replace('\\', '/');
            string file = System.IO.Path.GetFileNameWithoutExtension(path);

            foreach (var tag in q.Tags) if (!HasTag(note, tag)) return null;
            foreach (var tag in q.ExcludedTags) if (HasTag(note, tag)) return null;
            foreach (var p in q.Paths) if (path.IndexOf(p, StringComparison.OrdinalIgnoreCase) < 0) return null;
            foreach (var f in q.Files) if (file.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) return null;
            if (q.Vault != null && !string.Equals(note.VaultId, q.Vault, StringComparison.OrdinalIgnoreCase)) return null;

            if (q.Task != null)
            {
                var tasks = note.ExtractTasks();
                bool ok;
                switch (q.Task)
                {
                    case "todo": case "open": ok = tasks.Any(t => t.Status != TaskStatus.Done); break;
                    case "done": ok = tasks.Any(t => t.Status == TaskStatus.Done); break;
                    case "doing": case "progress": ok = tasks.Any(t => t.Status == TaskStatus.InProgress); break;
                    default: ok = tasks.Count > 0; break;
                }
                if (!ok) return null;
            }

            var cmp = q.TextComparison;
            foreach (var ex in q.Excluded)
            {
                if (Contains(title, ex, cmp) || Contains(body, ex, cmp) || note.Tags.Any(t => t.Equals(ex, cmp))) return null;
            }

            int score = 1;
            foreach (var phrase in q.Phrases)
            {
                bool inTitle = Contains(title, phrase, cmp);
                int inBody = CountOccurrences(body, phrase, cmp);
                if (!inTitle && inBody == 0) return null;
                score += (inTitle ? 20 : 0) + Math.Min(inBody, 5) * 3;
            }

            foreach (var term in q.Terms)
            {
                bool inTitle = Contains(title, term, cmp);
                int inBody = CountOccurrences(body, term, cmp);
                bool inTags = note.Tags.Any(t => t.IndexOf(term, cmp) >= 0);
                bool inPath = Contains(path, term, cmp);
                if (!inTitle && inBody == 0 && !inTags && !inPath) return null;

                score += (inTitle ? 15 : 0) + (title.StartsWith(term, cmp) ? 10 : 0) +
                         Math.Min(inBody, 5) * 2 + (inTags ? 6 : 0) + (inPath ? 2 : 0);
            }

            result.Score = score;
            CollectMatches(result, body, q.HighlightTerms.ToList(), cmp);
            return result;
        }

        /// <summary>Every body line with a term, as a snippet around its earliest hit.</summary>
        private static void CollectMatches(NoteSearchResult result, string body, List<string> terms, StringComparison cmp)
        {
            if (terms.Count == 0 || body.Length == 0) return;

            var lines = MarkdownScan.NormalizeNewlines(body).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int idx = -1, length = 0;
                foreach (var term in terms)
                {
                    int at = line.IndexOf(term, cmp);
                    if (at >= 0 && (idx < 0 || at < idx)) { idx = at; length = term.Length; }
                }
                if (idx < 0) continue;

                if (result.Matches.Count >= MaxMatchesPerNote) { result.MoreMatches++; continue; }
                result.Matches.Add(MakeSnippet(i, line, idx, length));
            }

            if (result.Matches.Count == 0) return;
            var first = result.Matches[0];
            result.Line = first.Line;
            result.Snippet = first.Snippet;
            result.SnippetMatchStart = first.MatchStart;
            result.SnippetMatchLength = first.MatchLength;
        }

        private static NoteSearchMatch MakeSnippet(int lineIndex, string line, int idx, int length)
        {
            int start = Math.Max(0, idx - SnippetRadius);
            int end = Math.Min(line.Length, idx + length + SnippetRadius);
            string prefix = start > 0 ? "…" : "";
            string window = line.Substring(start, end - start);
            int trimmedLead = window.Length - window.TrimStart().Length;
            return new NoteSearchMatch
            {
                Line = lineIndex,
                Snippet = prefix + window.Trim() + (end < line.Length ? "…" : ""),
                MatchStart = prefix.Length + idx - start - trimmedLead,
                MatchLength = length
            };
        }

        private static bool HasTag(NoteItem note, string tag) =>
            note.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase) || t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase));

        private static bool Contains(string text, string term, StringComparison cmp) => text.IndexOf(term, cmp) >= 0;

        private static int CountOccurrences(string text, string term, StringComparison cmp)
        {
            if (string.IsNullOrEmpty(term)) return 0;
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(term, idx, cmp)) >= 0)
            {
                count++;
                idx += term.Length;
                if (count >= 50) break;
            }
            return count;
        }
    }
}
