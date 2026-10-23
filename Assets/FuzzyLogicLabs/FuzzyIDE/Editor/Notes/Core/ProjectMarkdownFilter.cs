using System;
using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// Decides which .md files of the Assets folder the Assets vault lists. Pure logic, unit tested.
    /// A pattern without '/' matches any folder or file name ("Plugins", "LICENSE*"); a pattern with '/' matches a
    /// path from the Assets folder and everything below it ("Vendor/Docs", "Assets/Docs", "Assets/README.md").
    /// '*' and '?' are wildcards.
    /// </summary>
    public static class ProjectMarkdownFilter
    {
        /// <param name="relativePath">Path relative to the Assets folder with forward slashes ("Docs/Readme.md").</param>
        public static bool IsIncluded(string relativePath, IEnumerable<string> excludes)
        {
            if (string.IsNullOrEmpty(relativePath) || !relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
            string path = relativePath.Replace('\\', '/').Trim('/');
            var segments = path.Split('/');

            // Unity does not import dot-folders and folders ending in '~' either.
            foreach (var s in segments)
            {
                if (s.Length == 0 || s.StartsWith(".") || s.EndsWith("~")) return false;
            }

            if (excludes == null) return true;
            foreach (var raw in excludes)
            {
                string pattern = (raw ?? "").Trim().Replace('\\', '/').Trim('/');
                if (pattern.Length == 0) continue;
                // Decided before dropping the prefix: "Assets/Docs" means the top-level Docs folder only.
                bool isPath = pattern.IndexOf('/') >= 0;
                if (pattern.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) pattern = pattern.Substring("Assets/".Length);

                if (isPath)
                {
                    if (MatchesPathPrefix(segments, pattern.Split('/'))) return false;
                }
                else
                {
                    foreach (var s in segments)
                    {
                        if (Wildcard(s, pattern)) return false;
                    }
                }
            }
            return true;
        }

        private static bool MatchesPathPrefix(string[] segments, string[] pattern)
        {
            if (pattern.Length > segments.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (!Wildcard(segments[i], pattern[i])) return false;
            }
            return true;
        }

        /// <summary>Case-insensitive match with '*' (any run) and '?' (one character).</summary>
        public static bool Wildcard(string text, string pattern)
        {
            int t = 0, p = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
                {
                    t++;
                    p++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = t;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++mark;
                }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
    }
}
