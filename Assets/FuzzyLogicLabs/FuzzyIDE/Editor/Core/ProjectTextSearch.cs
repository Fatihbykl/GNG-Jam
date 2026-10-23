using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public sealed class TextSearchOptions
    {
        public string Query = "";
        public bool MatchCase;
        public bool WholeWord;
        public bool UseRegex;
        /// <summary>Comma separated path filters: "*.cs, Scripts/" includes, "!Tests/" excludes. Empty = everything.</summary>
        public string Include = "";
    }

    public struct TextSearchMatch
    {
        public string FilePath;
        public int Line;      // 0-based
        public int Column;    // 0-based
        public int Length;
        public string LineText;
    }

    /// <summary>Find / replace across files (the "Find in Files" panel). Line based; pure logic so it can run off the main thread.</summary>
    public static class ProjectTextSearch
    {
        /// <summary>Builds the matcher for the options, or null when the query is empty or an invalid regex.</summary>
        public static Regex BuildPattern(TextSearchOptions options, out string error)
        {
            error = null;
            if (options == null || string.IsNullOrEmpty(options.Query)) return null;

            string pattern = options.UseRegex ? options.Query : Regex.Escape(options.Query);
            if (options.WholeWord) pattern = @"(?<![\w])(?:" + pattern + @")(?![\w])";
            var flags = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            try
            {
                return new Regex(pattern, flags, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static bool PathMatchesFilter(string path, string include)
        {
            if (string.IsNullOrWhiteSpace(include)) return true;
            string normalized = path.Replace('\\', '/');

            bool hasIncludes = false, included = false;
            foreach (var raw in include.Split(','))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                bool exclude = token[0] == '!';
                if (exclude) token = token.Substring(1).Trim();
                if (token.Length == 0) continue;

                bool match = GlobMatch(normalized, token.Replace('\\', '/'));
                if (exclude && match) return false;
                if (!exclude)
                {
                    hasIncludes = true;
                    included |= match;
                }
            }
            return !hasIncludes || included;
        }

        // Without wildcards a token matches anywhere in the path ("Scripts/", "Enemy"). With '*' / '?' it is a glob on
        // the file name ("*.cs"), or on the end of the path when it contains a '/' ("Scripts/*.cs").
        private static bool GlobMatch(string path, string token)
        {
            if (token.IndexOf('*') < 0 && token.IndexOf('?') < 0)
                return path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

            string glob = Regex.Escape(token).Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
            if (token.IndexOf('/') < 0)
            {
                int slash = path.LastIndexOf('/');
                return Regex.IsMatch(slash >= 0 ? path.Substring(slash + 1) : path, "^" + glob + "$", RegexOptions.IgnoreCase);
            }
            return Regex.IsMatch(path, "(^|/)" + glob + "$", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Searches each file's text line by line. <paramref name="getText"/> returns the text to search (an open editor
        /// buffer or the file on disk) or null to skip the file. Stops after <paramref name="maxResults"/> matches.
        /// </summary>
        public static List<TextSearchMatch> Search(IEnumerable<string> files, Func<string, string> getText, TextSearchOptions options,
            int maxResults, CancellationToken cancellationToken, out bool truncated)
        {
            truncated = false;
            var results = new List<TextSearchMatch>();
            var pattern = BuildPattern(options, out _);
            if (pattern == null) return results;

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!PathMatchesFilter(file, options.Include)) continue;

                string text;
                try { text = getText(file); }
                catch { continue; }
                if (string.IsNullOrEmpty(text)) continue;

                int lineIndex = 0;
                foreach (var line in SplitLines(text))
                {
                    try
                    {
                        for (var m = pattern.Match(line); m.Success; m = m.NextMatch())
                        {
                            if (m.Length == 0) break; // e.g. "^" would match every position
                            results.Add(new TextSearchMatch { FilePath = file, Line = lineIndex, Column = m.Index, Length = m.Length, LineText = line });
                            if (results.Count >= maxResults)
                            {
                                truncated = true;
                                return results;
                            }
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // Pathological pattern on this line: skip it rather than hang the search.
                    }
                    lineIndex++;
                }
            }
            return results;
        }

        /// <summary>Replaces every match in <paramref name="text"/> (line by line, so matches never span lines). Regex replacements may use $1.</summary>
        public static string Replace(string text, TextSearchOptions options, string replacement, out int count)
        {
            count = 0;
            var pattern = BuildPattern(options, out _);
            if (pattern == null || text == null) return text;

            string safeReplacement = options.UseRegex ? replacement ?? "" : (replacement ?? "").Replace("$", "$$");
            var sb = new StringBuilder(text.Length);
            int start = 0;
            int replaced = 0;
            while (start <= text.Length)
            {
                int newline = text.IndexOf('\n', start);
                int end = newline < 0 ? text.Length : newline;
                int contentEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;

                string line = text.Substring(start, contentEnd - start);
                string newLine = pattern.Replace(line, m =>
                {
                    if (m.Length == 0) return m.Value;
                    replaced++;
                    return m.Result(safeReplacement);
                });
                sb.Append(newLine);
                sb.Append(text, contentEnd, end - contentEnd); // keep '\r'
                if (newline < 0) break;
                sb.Append('\n');
                start = newline + 1;
            }
            count = replaced;
            return sb.ToString();
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            int start = 0;
            while (start <= text.Length)
            {
                int newline = text.IndexOf('\n', start);
                int end = newline < 0 ? text.Length : newline;
                int contentEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;
                yield return text.Substring(start, contentEnd - start);
                if (newline < 0) yield break;
                start = newline + 1;
            }
        }
    }
}
