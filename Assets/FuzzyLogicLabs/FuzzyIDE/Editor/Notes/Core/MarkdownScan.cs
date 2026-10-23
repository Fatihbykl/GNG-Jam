using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// Code-aware scanning helpers for note bodies: inline #tags, [[wiki links]] and link rewriting.
    /// Fenced code blocks and inline code spans are always skipped. Input uses '\n' line endings.
    /// </summary>
    public static class MarkdownScan
    {
        private static readonly Regex InlineCodeRx = new Regex(@"(`+)[\s\S]*?\1", RegexOptions.Compiled);
        // Obsidian rules: '#' at start or after whitespace; letters/digits/_/-/ allowed (any script).
        private static readonly Regex InlineTagRx = new Regex(@"(?<=^|\s)#([\p{L}\p{N}_\-/]+)", RegexOptions.Compiled);
        private static readonly Regex WikiLinkRx = new Regex(@"\[\[([^\[\]\r\n]+?)\]\]", RegexOptions.Compiled);

        public static string NormalizeNewlines(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("\r\n", "\n").Replace('\r', '\n');

        public static List<string> ExtractInlineTags(string body)
        {
            var result = new List<string>();
            ForEachProseLine(body, (line, _) =>
            {
                foreach (Match m in InlineTagRx.Matches(MaskInlineCode(line)))
                {
                    string tag = m.Groups[1].Value.TrimEnd('/', '-').ToLowerInvariant();
                    if (tag.Length == 0 || IsAllDigits(tag) || result.Contains(tag)) continue;
                    result.Add(tag);
                }
            });
            return result;
        }

        /// <summary>Raw inner text of every [[link]] / ![[embed]] outside code.</summary>
        public static List<string> ExtractWikiLinks(string body)
        {
            var result = new List<string>();
            ForEachProseLine(body, (line, _) =>
            {
                foreach (Match m in WikiLinkRx.Matches(MaskInlineCode(line)))
                {
                    string inner = m.Groups[1].Value.Trim();
                    if (inner.Length > 0) result.Add(inner);
                }
            });
            return result;
        }

        /// <summary>
        /// Reduces a link's inner text to the note it points at:
        /// "Folder\Note.md#Heading|Alias" → "Folder/Note".
        /// </summary>
        public static string NormalizeLinkTarget(string inner)
        {
            if (string.IsNullOrEmpty(inner)) return "";
            string t = inner;
            int pipe = t.IndexOf('|');
            if (pipe >= 0) t = t.Substring(0, pipe);
            int hash = t.IndexOf('#');
            if (hash >= 0) t = t.Substring(0, hash);
            t = t.Trim().Replace('\\', '/');
            if (t.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 3);
            return t;
        }

        /// <summary>
        /// Rewrites the target part of each wiki link outside code. <paramref name="map"/> receives the
        /// normalized target and returns the replacement target, or null to leave the link as is.
        /// Heading (#...) and alias (|...) suffixes are preserved.
        /// </summary>
        public static string RewriteWikiLinks(string body, Func<string, string> map, out int replacedCount)
        {
            int count = 0;
            var lines = NormalizeNewlines(body).Split('\n');
            var states = BlockParser.ComputeCodeStates(lines);

            for (int i = 0; i < lines.Length; i++)
            {
                if (states[i] != BlockParser.StateNone || lines[i].IndexOf("[[", StringComparison.Ordinal) < 0) continue;

                string line = lines[i];
                string masked = MaskInlineCode(line);
                var sb = new StringBuilder();
                int last = 0;
                foreach (Match m in WikiLinkRx.Matches(masked))
                {
                    var g = m.Groups[1];
                    string inner = line.Substring(g.Index, g.Length);
                    int suffixAt = inner.IndexOfAny(new[] { '#', '|' });
                    string targetPart = suffixAt >= 0 ? inner.Substring(0, suffixAt) : inner;
                    string replacement = map(NormalizeLinkTarget(targetPart));
                    if (replacement == null) continue;

                    sb.Append(line, last, g.Index - last);
                    sb.Append(replacement);
                    if (suffixAt >= 0) sb.Append(inner, suffixAt, inner.Length - suffixAt);
                    last = g.Index + g.Length;
                    count++;
                }
                if (last > 0)
                {
                    sb.Append(line, last, line.Length - last);
                    lines[i] = sb.ToString();
                }
            }

            replacedCount = count;
            return count > 0 ? string.Join("\n", lines) : body;
        }

        /// <summary>
        /// Splits a link's inner text: "Folder/Note#Heading|Alias" → note "Folder/Note", subpath "Heading", alias "Alias".
        /// Block references keep their caret ("Note#^id" → subpath "^id"). Empty parts are "".
        /// </summary>
        public static void SplitLinkTarget(string inner, out string note, out string subpath, out string alias)
        {
            string t = inner ?? "";
            alias = "";
            int pipe = t.IndexOf('|');
            if (pipe >= 0)
            {
                alias = t.Substring(pipe + 1).Trim();
                t = t.Substring(0, pipe);
            }

            subpath = "";
            int hash = t.IndexOf('#');
            if (hash >= 0)
            {
                subpath = t.Substring(hash + 1).Trim();
                t = t.Substring(0, hash);
            }
            note = t.Trim();
        }

        /// <summary>
        /// Body line index targeted by a link subpath: a heading ("Setup", case/format-insensitive)
        /// or a block id ("^abc123", matching a line ending in " ^abc123"). -1 when not found.
        /// </summary>
        public static int FindSubpathLine(string body, string subpath)
        {
            if (string.IsNullOrWhiteSpace(subpath)) return -1;
            subpath = subpath.Trim();
            bool block = subpath.StartsWith("^");
            string wanted = block ? subpath : NormalizeHeading(subpath);
            int found = -1;

            ForEachProseLine(body, (line, i) =>
            {
                if (found >= 0) return;
                if (block)
                {
                    string trimmed = line.TrimEnd();
                    if (trimmed.EndsWith(" " + wanted, StringComparison.Ordinal) || trimmed == wanted) found = i;
                }
                else if (line.StartsWith("#"))
                {
                    int level = 0;
                    while (level < line.Length && line[level] == '#') level++;
                    if (level <= 6 && level < line.Length && line[level] == ' ' && NormalizeHeading(line.Substring(level)) == wanted) found = i;
                }
            });
            return found;
        }

        private static string NormalizeHeading(string text)
        {
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in text.Trim())
            {
                if (c == '*' || c == '_' || c == '`' || c == '~' || c == '[' || c == ']') continue;
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>Plain-text mentions of any of <paramref name="names"/> outside code and existing links.</summary>
        public static int CountUnlinkedMentions(string body, IEnumerable<string> names)
        {
            LinkUnlinkedMentions(body, names, "x", out int count);
            return count;
        }

        /// <summary>
        /// Turns plain-text mentions of <paramref name="names"/> into [[links]] to <paramref name="linkTarget"/>
        /// (whole words, case-insensitive, never inside code, existing links or URLs).
        /// A mention written differently (even only in case) keeps its wording as an alias: [[Target|mention]].
        /// </summary>
        public static string LinkUnlinkedMentions(string body, IEnumerable<string> names, string linkTarget, out int count)
        {
            count = 0;
            var valid = new List<string>();
            foreach (var n in names)
            {
                if (!string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 3 && !valid.Contains(n.Trim())) valid.Add(n.Trim());
            }
            if (valid.Count == 0 || string.IsNullOrEmpty(body)) return body;
            valid.Sort((a, b) => b.Length.CompareTo(a.Length)); // longest name wins

            var nameRx = new Regex(@"(?<![\p{L}\p{N}_])(?:" + string.Join("|", valid.ConvertAll(Regex.Escape)) + @")(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase);
            var lines = NormalizeNewlines(body).Split('\n');
            var states = BlockParser.ComputeCodeStates(lines);
            int total = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                if (states[i] != BlockParser.StateNone) continue;
                string line = lines[i];
                string masked = MaskProtectedSpans(line);
                var matches = nameRx.Matches(masked);
                if (matches.Count == 0) continue;

                var sb = new StringBuilder();
                int last = 0;
                foreach (Match m in matches)
                {
                    string original = line.Substring(m.Index, m.Length);
                    sb.Append(line, last, m.Index - last);
                    sb.Append(string.Equals(original, linkTarget, StringComparison.Ordinal)
                        ? "[[" + linkTarget + "]]"
                        : "[[" + linkTarget + "|" + original + "]]");
                    last = m.Index + m.Length;
                    total++;
                }
                sb.Append(line, last, line.Length - last);
                lines[i] = sb.ToString();
            }

            count = total;
            return total > 0 ? string.Join("\n", lines) : body;
        }

        private static readonly Regex ProtectedSpanRx = new Regex(@"(`+)[\s\S]*?\1|!?\[\[[^\]]*\]\]|\[[^\]]*\]\([^)]*\)|https?://\S+|(?<=^|\s)#[\p{L}\p{N}_\-/]+", RegexOptions.Compiled);

        /// <summary>Blanks out inline code, links, URLs and tags so mention matching cannot touch them.</summary>
        private static string MaskProtectedSpans(string line) =>
            ProtectedSpanRx.Replace(line, m => new string(' ', m.Length));

        /// <summary>Invokes <paramref name="action"/>(line, lineIndex) for every line outside fenced code.</summary>
        public static void ForEachProseLine(string text, Action<string, int> action)
        {
            if (string.IsNullOrEmpty(text)) return;
            var lines = NormalizeNewlines(text).Split('\n');
            var states = BlockParser.ComputeCodeStates(lines);
            for (int i = 0; i < lines.Length; i++)
            {
                if (states[i] == BlockParser.StateNone) action(lines[i], i);
            }
        }

        /// <summary>Replaces inline code spans with spaces so indices stay aligned with the original line.</summary>
        private static string MaskInlineCode(string line)
        {
            if (line.IndexOf('`') < 0) return line;
            return InlineCodeRx.Replace(line, m => new string(' ', m.Length));
        }

        private static bool IsAllDigits(string s)
        {
            foreach (char c in s) if (!char.IsDigit(c)) return false;
            return true;
        }
    }
}
