using System;
using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>Bracket pairing for the editor's matching-bracket highlight.</summary>
    public static class BracketMatcher
    {
        private const string OpenBrackets = "([{";
        private const string CloseBrackets = ")]}";
        private const int MaxBracketScanLines = 3000;

        /// <summary>
        /// Finds the bracket next to the caret (the one before it wins, like VS Code) and its partner. Brackets inside
        /// strings, chars and line comments on the scanned lines are ignored.
        /// </summary>
        public static bool TryFindMatchingBracket(IList<string> lines, int caretLine, int caretColumn,
            out int line, out int col, out int matchLine, out int matchCol)
        {
            line = col = matchLine = matchCol = -1;
            string text = lines[caretLine];

            int[] candidates = { caretColumn - 1, caretColumn };
            foreach (int c in candidates)
            {
                if (c < 0 || c >= text.Length) continue;
                char ch = text[c];
                int open = OpenBrackets.IndexOf(ch);
                int close = CloseBrackets.IndexOf(ch);
                if (open < 0 && close < 0) continue;
                if (!IsCode(text, c)) continue;

                line = caretLine;
                col = c;
                bool forward = open >= 0;
                char self = ch;
                char partner = forward ? CloseBrackets[open] : OpenBrackets[close];
                return forward
                    ? ScanForward(lines, caretLine, c + 1, self, partner, out matchLine, out matchCol)
                    : ScanBackward(lines, caretLine, c - 1, self, partner, out matchLine, out matchCol);
            }
            return false;
        }

        private static bool ScanForward(IList<string> lines, int startLine, int startCol, char self, char partner, out int matchLine, out int matchCol)
        {
            int depth = 0;
            int lastLine = Math.Min(lines.Count - 1, startLine + MaxBracketScanLines);
            for (int l = startLine; l <= lastLine; l++)
            {
                string text = lines[l];
                var mask = CodeMask(text);
                for (int c = l == startLine ? startCol : 0; c < text.Length; c++)
                {
                    if (!mask[c]) continue;
                    if (text[c] == self) depth++;
                    else if (text[c] == partner)
                    {
                        if (depth == 0) { matchLine = l; matchCol = c; return true; }
                        depth--;
                    }
                }
            }
            matchLine = matchCol = -1;
            return false;
        }

        private static bool ScanBackward(IList<string> lines, int startLine, int startCol, char self, char partner, out int matchLine, out int matchCol)
        {
            int depth = 0;
            int firstLine = Math.Max(0, startLine - MaxBracketScanLines);
            for (int l = startLine; l >= firstLine; l--)
            {
                string text = lines[l];
                var mask = CodeMask(text);
                for (int c = l == startLine ? startCol : text.Length - 1; c >= 0; c--)
                {
                    if (!mask[c]) continue;
                    if (text[c] == self) depth++;
                    else if (text[c] == partner)
                    {
                        if (depth == 0) { matchLine = l; matchCol = c; return true; }
                        depth--;
                    }
                }
            }
            matchLine = matchCol = -1;
            return false;
        }

        /// <summary>True when the character at <paramref name="index"/> is code (not in a string/char literal or // comment).</summary>
        public static bool IsCode(string text, int index) => CodeMask(text)[index];

        // true = code character; false = inside "..." / '...' / a // comment (per line, which covers the common cases).
        private static bool[] CodeMask(string text)
        {
            var mask = new bool[text.Length];
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0')
                {
                    mask[i] = false;
                    if (ch == '\\') { if (i + 1 < text.Length) mask[++i] = false; }
                    else if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/') break;
                if (ch == '"' || ch == '\'') { quote = ch; mask[i] = false; continue; }
                mask[i] = true;
            }
            return mask;
        }
    }
}
