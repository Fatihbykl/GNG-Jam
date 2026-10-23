using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor
{
    public enum BlockKind
    {
        Paragraph,
        Heading,
        Bullet,
        Numbered,
        Task,
        Quote,
        HorizontalRule,
        CodeFence,
        CodeLine,
        Table,
        TableSeparator,
        Image
    }

    /// <summary>Result of classifying a single raw markdown line.</summary>
    public struct BlockInfo
    {
        public BlockKind Kind;
        /// <summary>Heading level 1-6 (Heading only).</summary>
        public int Level;
        /// <summary>List nesting level derived from leading whitespace.</summary>
        public int IndentLevel;
        /// <summary>Number of raw characters forming the markdown marker (hidden in Live Preview).</summary>
        public int PrefixLen;
        /// <summary>Text drawn in place of the marker when the line is not being edited.</summary>
        public string Glyph;
        /// <summary>Task state char: ' ', '/', or 'x' (Task only).</summary>
        public char TaskMark;
        /// <summary>Raw index of the task state char (Task only), otherwise -1.</summary>
        public int TaskMarkIndex;
        /// <summary>
        /// Kind-specific payload: code fence language, callout type (Quote, lower-case) or image target (Image).
        /// Null when not applicable.
        /// </summary>
        public string Target;
        public bool IsCallout => Kind == BlockKind.Quote && Target != null;
    }

    public static class BlockParser
    {
        public const byte StateNone = 0;
        public const byte StateFence = 1;
        public const byte StateCode = 2;

        private static readonly Regex HeadingRx = new Regex(@"^(#{1,6})[ \t]+", RegexOptions.Compiled);
        private static readonly Regex HrRx = new Regex(@"^[ \t]*([-*_])([ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex TaskRx = new Regex(@"^([ \t]*)[-*+][ \t]+\[([ xX/])\](?:[ \t]|$)", RegexOptions.Compiled);
        private static readonly Regex BulletRx = new Regex(@"^([ \t]*)[-*+][ \t]", RegexOptions.Compiled);
        private static readonly Regex NumberRx = new Regex(@"^([ \t]*)(\d+)[.)][ \t]", RegexOptions.Compiled);
        private static readonly Regex QuoteRx = new Regex(@"^[ \t]*>[ \t]?", RegexOptions.Compiled);
        private static readonly Regex CalloutRx = new Regex(@"^[ \t]*>[ \t]?\[!([A-Za-z][\w-]*)\][+-]?[ \t]*", RegexOptions.Compiled);
        private static readonly Regex TableSeparatorRx = new Regex(@"^[ \t]*\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)+\|?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex WikiImageRx = new Regex(@"^[ \t]*!\[\[([^\]|]+)(?:\|[^\]]*)?\]\][ \t]*$", RegexOptions.Compiled);
        private static readonly Regex MdImageRx = new Regex(@"^[ \t]*!\[[^\]]*\]\(([^)\s]+)(?:[ \t]+""[^""]*"")?\)[ \t]*$", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> CalloutGlyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "note", "✎ " }, { "info", "ℹ " }, { "abstract", "☰ " }, { "summary", "☰ " }, { "todo", "☐ " },
            { "tip", "💡 " }, { "hint", "💡 " }, { "important", "💡 " }, { "success", "✔ " }, { "check", "✔ " }, { "done", "✔ " },
            { "question", "? " }, { "help", "? " }, { "faq", "? " }, { "warning", "⚠ " }, { "caution", "⚠ " }, { "attention", "⚠ " },
            { "failure", "✖ " }, { "fail", "✖ " }, { "missing", "✖ " }, { "danger", "⚡ " }, { "error", "⚡ " }, { "bug", "🐞 " },
            { "example", "≡ " }, { "quote", "❝ " }, { "cite", "❝ " }
        };

        public static string CalloutGlyph(string type) => type != null && CalloutGlyphs.TryGetValue(type, out var g) ? g : "✎ ";

        public static bool IsShellLanguage(string lang)
        {
            switch ((lang ?? "").Trim().ToLowerInvariant())
            {
                case "sh": case "bash": case "shell": case "zsh": case "console": case "terminal":
                case "ps": case "ps1": case "powershell": case "pwsh": case "cmd": case "bat":
                    return true;
                default:
                    return false;
            }
        }

        public const string GlyphBullet = "• ";
        public const string GlyphTaskOpen = "○ ";
        public const string GlyphTaskProgress = "◐ ";
        public const string GlyphTaskDone = "✔ ";

        /// <summary>Marks fenced code regions: 0 = normal, 1 = fence line (```), 2 = inside code.</summary>
        public static byte[] ComputeCodeStates(IReadOnlyList<string> lines)
        {
            var states = new byte[lines.Count];
            bool inCode = false;
            for (int i = 0; i < lines.Count; i++)
            {
                bool fence = lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal);
                if (fence)
                {
                    states[i] = StateFence;
                    inCode = !inCode;
                }
                else states[i] = inCode ? StateCode : StateNone;
            }
            return states;
        }

        public static BlockInfo Parse(string raw, byte codeState)
        {
            raw = raw ?? "";
            var info = new BlockInfo { Kind = BlockKind.Paragraph, Glyph = "", TaskMarkIndex = -1, TaskMark = ' ' };

            if (codeState == StateFence)
            {
                info.Kind = BlockKind.CodeFence;
                string fence = raw.Trim();
                info.Target = fence.Length > 3 ? fence.Substring(3).Trim() : "";
                return info;
            }
            if (codeState == StateCode) { info.Kind = BlockKind.CodeLine; return info; }

            var m = HeadingRx.Match(raw);
            if (m.Success)
            {
                info.Kind = BlockKind.Heading;
                info.Level = m.Groups[1].Length;
                info.PrefixLen = m.Length;
                return info;
            }

            if (HrRx.IsMatch(raw))
            {
                info.Kind = BlockKind.HorizontalRule;
                info.PrefixLen = raw.Length;
                return info;
            }

            m = TaskRx.Match(raw);
            if (m.Success)
            {
                char c = m.Groups[2].Value[0];
                info.Kind = BlockKind.Task;
                info.IndentLevel = IndentOf(m.Groups[1].Value);
                info.PrefixLen = m.Length;
                info.TaskMark = c == '/' ? '/' : (c == 'x' || c == 'X') ? 'x' : ' ';
                info.TaskMarkIndex = m.Groups[2].Index;
                info.Glyph = info.TaskMark == 'x' ? GlyphTaskDone : info.TaskMark == '/' ? GlyphTaskProgress : GlyphTaskOpen;
                return info;
            }

            m = BulletRx.Match(raw);
            if (m.Success)
            {
                info.Kind = BlockKind.Bullet;
                info.IndentLevel = IndentOf(m.Groups[1].Value);
                info.PrefixLen = m.Length;
                info.Glyph = GlyphBullet;
                return info;
            }

            m = NumberRx.Match(raw);
            if (m.Success)
            {
                info.Kind = BlockKind.Numbered;
                info.IndentLevel = IndentOf(m.Groups[1].Value);
                info.PrefixLen = m.Length;
                info.Glyph = m.Groups[2].Value + ". ";
                return info;
            }

            m = CalloutRx.Match(raw);
            if (m.Success)
            {
                info.Kind = BlockKind.Quote;
                info.PrefixLen = m.Length;
                info.Target = m.Groups[1].Value.ToLowerInvariant();
                info.Glyph = CalloutGlyph(info.Target);
                return info;
            }

            m = QuoteRx.Match(raw);
            if (m.Success)
            {
                info.Kind = BlockKind.Quote;
                info.PrefixLen = m.Length;
                return info;
            }

            if (raw.IndexOf('|') >= 0)
            {
                if (TableSeparatorRx.IsMatch(raw)) { info.Kind = BlockKind.TableSeparator; return info; }
                if (raw.TrimStart().StartsWith("|", StringComparison.Ordinal)) { info.Kind = BlockKind.Table; return info; }
            }

            m = WikiImageRx.Match(raw);
            if (!m.Success) m = MdImageRx.Match(raw);
            if (m.Success)
            {
                info.Kind = BlockKind.Image;
                info.Target = m.Groups[1].Value.Trim();
                return info;
            }

            return info;
        }

        private static int IndentOf(string ws)
        {
            int cols = 0;
            foreach (char c in ws) cols += c == '\t' ? 2 : 1;
            return cols / 2;
        }

        /// <summary>Next task state in the click cycle: open -> in progress -> done -> open.</summary>
        public static char NextTaskMark(char current) => current == ' ' ? '/' : current == '/' ? 'x' : ' ';

        /// <summary>Splits a markdown table row into trimmed cell texts ("| a | b |" → ["a", "b"]). "\|" is a literal pipe.</summary>
        public static List<string> SplitTableCells(string raw)
        {
            var cells = new List<string>();
            string t = (raw ?? "").Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|") && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|') { sb.Append('|'); i++; continue; }
                if (c == '|') { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(c);
            }
            cells.Add(sb.ToString().Trim());
            return cells;
        }
    }

    /// <summary>Greedy word-wrapper working on a caller-supplied width measure (testable without UI).</summary>
    public static class LineWrapper
    {
        public static List<(int start, int end)> Wrap(string text, Func<string, float> measure, float avail)
        {
            var rows = new List<(int, int)>();
            text = text ?? "";
            if (text.Length == 0 || avail <= 0f || measure(text) <= avail)
            {
                rows.Add((0, text.Length));
                return rows;
            }

            int pos = 0;
            while (pos < text.Length)
            {
                if (measure(text.Substring(pos)) <= avail)
                {
                    rows.Add((pos, text.Length));
                    break;
                }

                // largest end (> pos) such that text[pos..end] fits
                int lo = pos + 1, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (measure(text.Substring(pos, mid - pos)) <= avail) lo = mid; else hi = mid - 1;
                }
                int end = lo;

                if (end < text.Length)
                {
                    if (text[end] == ' ')
                    {
                        end++; // the space hangs at the end of this row
                    }
                    else
                    {
                        int sp = text.LastIndexOf(' ', end - 1, end - pos);
                        if (sp > pos) end = sp + 1;
                    }
                }

                rows.Add((pos, end));
                pos = end;
            }
            return rows;
        }
    }
}
