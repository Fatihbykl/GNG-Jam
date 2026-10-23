using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// Order- and format-preserving YAML front-matter block (the text between the two <c>---</c> fences).
    /// Only entries that are explicitly changed are re-emitted; unknown keys, comments and
    /// formatting round-trip untouched, so notes written by Obsidian or other tools never lose data.
    /// Keys are matched case-insensitively. Lines are always '\n' separated.
    /// </summary>
    public sealed class FrontMatter
    {
        private sealed class Entry
        {
            public string Key;  // null for comments, blank lines and anything we could not parse
            public readonly List<string> Lines = new List<string>();
        }

        // A top-level key line: starts at column 0, not a list item / comment, "key:" optionally followed by a value.
        private static readonly Regex KeyLineRx = new Regex(@"^([^\s#\-:][^:]*?)[ \t]*:(?:[ \t]+(.*?)|)[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex BlockItemRx = new Regex(@"^([ \t]*)-[ \t]*(.*?)[ \t]*$", RegexOptions.Compiled);

        private readonly List<Entry> m_Entries = new List<Entry>();

        public bool IsEmpty => m_Entries.Count == 0;

        public static FrontMatter Parse(string block)
        {
            var fm = new FrontMatter();
            if (string.IsNullOrEmpty(block)) return fm;

            var lines = block.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Entry current = null;
            foreach (var line in lines)
            {
                bool isContinuation = current != null && current.Key != null && line.Length > 0 &&
                                      (line[0] == ' ' || line[0] == '\t' || line[0] == '-');
                if (isContinuation)
                {
                    current.Lines.Add(line);
                    continue;
                }

                var m = KeyLineRx.Match(line);
                current = new Entry { Key = m.Success ? m.Groups[1].Value.Trim() : null };
                current.Lines.Add(line);
                fm.m_Entries.Add(current);
            }
            return fm;
        }

        public bool Has(string key) => Find(key) != null;

        /// <summary>Keys in file order.</summary>
        public List<string> Keys
        {
            get
            {
                var keys = new List<string>();
                foreach (var e in m_Entries) if (e.Key != null) keys.Add(e.Key);
                return keys;
            }
        }

        /// <summary>True when the value is a YAML list (inline <c>[a, b]</c> or block <c>- a</c>).</summary>
        public bool IsList(string key)
        {
            var e = Find(key);
            if (e == null) return false;
            string inline = InlineValue(e);
            if (inline.Length > 0) return inline.StartsWith("[") && inline.EndsWith("]");
            return e.Lines.Count > 1 && BlockItemRx.IsMatch(e.Lines[1]);
        }

        /// <summary>Unquoted scalar value, or null if the key is missing or holds a block list/map.</summary>
        public string GetScalar(string key)
        {
            var e = Find(key);
            if (e == null) return null;
            string inline = InlineValue(e);
            if (inline.Length == 0) return e.Lines.Count > 1 ? null : "";
            return Unquote(inline);
        }

        /// <summary>
        /// Reads a list in any of the common YAML shapes: <c>[a, b]</c>, a block list (<c>- a</c>),
        /// or a plain comma-separated scalar. When <paramref name="splitOnWhitespace"/> is set a scalar
        /// is also split on spaces (Obsidian accepts <c>tags: a b</c>).
        /// </summary>
        public List<string> GetList(string key, bool splitOnWhitespace = false)
        {
            var result = new List<string>();
            var e = Find(key);
            if (e == null) return result;

            string inline = InlineValue(e);
            if (inline.Length > 0)
            {
                if (inline.StartsWith("[") && inline.EndsWith("]"))
                {
                    foreach (var part in SplitFlowList(inline.Substring(1, inline.Length - 2)))
                        AddItem(result, part);
                }
                else
                {
                    var separators = splitOnWhitespace ? new[] { ',', ' ', '\t' } : new[] { ',' };
                    if (inline.StartsWith("\"") || inline.StartsWith("'")) AddItem(result, inline);
                    else foreach (var part in inline.Split(separators, StringSplitOptions.RemoveEmptyEntries)) AddItem(result, part);
                }
                return result;
            }

            for (int i = 1; i < e.Lines.Count; i++)
            {
                var m = BlockItemRx.Match(e.Lines[i]);
                if (m.Success) AddItem(result, m.Groups[2].Value);
            }
            return result;
        }

        public void SetScalar(string key, string value, bool forceQuote = false)
        {
            string formatted = forceQuote ? Quote(value ?? "") : FormatScalar(value ?? "");
            var e = FindOrAdd(key);
            e.Lines.Clear();
            e.Lines.Add($"{e.Key}: {formatted}");
        }

        /// <summary>Writes a list, keeping the entry's existing style (block vs. inline) when it already exists.</summary>
        public void SetList(string key, IList<string> items)
        {
            var e = Find(key);
            bool blockStyle = false;
            string itemIndent = "  ";
            if (e != null && e.Lines.Count > 1 && InlineValue(e).Length == 0)
            {
                var m = BlockItemRx.Match(e.Lines[1]);
                if (m.Success)
                {
                    blockStyle = true;
                    itemIndent = m.Groups[1].Value;
                }
            }

            e = e ?? FindOrAdd(key);
            e.Lines.Clear();
            if (blockStyle && items.Count > 0)
            {
                e.Lines.Add($"{e.Key}:");
                foreach (var item in items) e.Lines.Add($"{itemIndent}- {FormatScalar(item)}");
            }
            else
            {
                var parts = new List<string>();
                foreach (var item in items) parts.Add(FormatListItem(item));
                e.Lines.Add($"{e.Key}: [{string.Join(", ", parts)}]");
            }
        }

        public void Remove(string key)
        {
            var e = Find(key);
            if (e != null) m_Entries.Remove(e);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < m_Entries.Count; i++)
            {
                var lines = m_Entries[i].Lines;
                for (int j = 0; j < lines.Count; j++)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(lines[j]);
                }
            }
            return sb.ToString();
        }

        private Entry Find(string key)
        {
            foreach (var e in m_Entries)
            {
                if (e.Key != null && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        private Entry FindOrAdd(string key)
        {
            var e = Find(key);
            if (e != null) return e;
            e = new Entry { Key = key };
            m_Entries.Add(e);
            return e;
        }

        private static string InlineValue(Entry e)
        {
            var m = KeyLineRx.Match(e.Lines[0]);
            return m.Success ? m.Groups[2].Value.Trim() : "";
        }

        private static void AddItem(List<string> list, string raw)
        {
            string v = Unquote(raw.Trim());
            if (v.Length > 0) list.Add(v);
        }

        private static IEnumerable<string> SplitFlowList(string content)
        {
            var sb = new StringBuilder();
            char quote = '\0';
            foreach (char c in content)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    sb.Append(c);
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                    sb.Append(c);
                }
                else if (c == ',')
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        public static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                return v.Substring(1, v.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (v.Length >= 2 && v[0] == '\'' && v[v.Length - 1] == '\'')
                return v.Substring(1, v.Length - 2).Replace("''", "'");
            return v;
        }

        public static string Quote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string FormatScalar(string v)
        {
            if (v.Length == 0) return "\"\"";
            bool needsQuote = v.Trim() != v ||
                              v.IndexOf(": ", StringComparison.Ordinal) >= 0 ||
                              v.IndexOf(" #", StringComparison.Ordinal) >= 0 ||
                              "#&*!|>'\"%@`[]{},-?".IndexOf(v[0]) >= 0;
            return needsQuote ? Quote(v) : v;
        }

        private static string FormatListItem(string v)
        {
            string f = FormatScalar(v);
            return f[0] != '"' && (v.IndexOf(',') >= 0 || v.IndexOf(']') >= 0) ? Quote(v) : f;
        }
    }
}
