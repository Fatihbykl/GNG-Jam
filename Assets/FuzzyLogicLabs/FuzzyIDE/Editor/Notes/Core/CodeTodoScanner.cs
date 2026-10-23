using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>A // TODO, FIXME, HACK or BUG comment in a code file.</summary>
    public sealed class CodeTodo
    {
        /// <summary>Project-relative path ("Assets/Scripts/PlayerMotor.cs").</summary>
        public string AssetPath;
        /// <summary>0-based line.</summary>
        public int Line;
        /// <summary>TODO, FIXME, HACK or BUG (upper case).</summary>
        public string Kind;
        public string Text;

        public string FileName => System.IO.Path.GetFileName(AssetPath ?? "");
        /// <summary>Note link to the comment, e.g. "PlayerMotor.cs:42".</summary>
        public string Link => FileName + ":" + (Line + 1);
    }

    /// <summary>Finds TODO-style comments in source text. Pure text logic, used by the live code TODO index.</summary>
    public static class CodeTodoScanner
    {
        // "//" not preceded by ':' (skips URLs), then the marker as a whole word.
        private static readonly Regex TodoRx = new Regex(
            @"(?<!:)//+\s*(TODO|FIXME|HACK|BUG)\b\s*(?:\([^)]*\))?\s*[:\-]?\s*(.*)$",
            RegexOptions.Compiled);

        public static readonly string[] Kinds = { "TODO", "FIXME", "HACK", "BUG" };

        public static List<CodeTodo> Scan(string assetPath, string text)
        {
            var result = new List<CodeTodo>();
            if (string.IsNullOrEmpty(text)) return result;

            int line = 0;
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                if (end - start > 6)
                {
                    string l = text.Substring(start, end - start);
                    if (l.IndexOf("//", StringComparison.Ordinal) >= 0)
                    {
                        var m = TodoRx.Match(l);
                        if (m.Success)
                        {
                            result.Add(new CodeTodo
                            {
                                AssetPath = assetPath,
                                Line = line,
                                Kind = m.Groups[1].Value,
                                Text = m.Groups[2].Value.Trim().TrimEnd('*', '/').Trim()
                            });
                        }
                    }
                }
                line++;
                start = i + 1;
            }
            return result;
        }
    }
}
