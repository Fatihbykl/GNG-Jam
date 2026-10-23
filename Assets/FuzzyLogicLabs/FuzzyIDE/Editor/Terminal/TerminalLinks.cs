using System;
using System.IO;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    /// <summary>
    /// Finds the URL or file reference under a character of a terminal line, for Ctrl+click
    /// (Cmd+click on macOS). File references may carry a line number in the forms compilers and CLIs print:
    /// path:12, path:12:5, path(12,5), path, line 12, path#L12.
    /// </summary>
    internal static class TerminalLinks
    {
        public enum Kind { None, Url, File }

        private static readonly Regex UrlPattern = new Regex(
            @"\b(?:https?|file|ftp)://[^\s""'<>`]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PathPattern = new Regex(
            @"(?<path>(?:[A-Za-z]:[\\/]|~[\\/]|\.{1,2}[\\/]|[\\/])?(?:[\w.\-+@]+[\\/])*[\w\-+@][\w.\-+@]*\.[A-Za-z0-9]+)" +
            @"(?:(?::|\(|, line |#L)(?<line>\d+))?",
            RegexOptions.Compiled);

        /// <summary>Returns the link covering <paramref name="index"/> in <paramref name="text"/>.</summary>
        public static Kind Find(string text, int index, out string target, out int line)
        {
            target = null;
            line = 0;
            if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length) return Kind.None;

            foreach (Match match in UrlPattern.Matches(text))
            {
                string url = TrimUrl(match.Value);
                if (index >= match.Index && index < match.Index + url.Length)
                {
                    target = url;
                    return Kind.Url;
                }
            }

            foreach (Match match in PathPattern.Matches(text))
            {
                if (index < match.Index || index >= match.Index + match.Length) continue;
                target = match.Groups["path"].Value;
                if (match.Groups["line"].Success) int.TryParse(match.Groups["line"].Value, out line);
                return Kind.File;
            }
            return Kind.None;
        }

        /// <summary>
        /// Resolves a file reference against the given directories (absolute paths and ~ as they are).
        /// Returns null when no such file or directory exists.
        /// </summary>
        public static string ResolvePath(string path, params string[] baseDirectories)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                {
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
                }

                if (Path.IsPathRooted(path))
                {
                    return Exists(path) ? Path.GetFullPath(path) : null;
                }

                foreach (string baseDirectory in baseDirectories)
                {
                    if (string.IsNullOrEmpty(baseDirectory)) continue;
                    string candidate = Path.GetFullPath(Path.Combine(baseDirectory, path));
                    if (Exists(candidate)) return candidate;
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return null;
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private static string TrimUrl(string url)
        {
            // Sentence punctuation and closing brackets that are not part of the URL, e.g. "(see https://x.y)."
            while (url.Length > 0)
            {
                char last = url[url.Length - 1];
                if (".,;:!?'\"".IndexOf(last) >= 0)
                {
                    url = url.Substring(0, url.Length - 1);
                    continue;
                }
                if ((last == ')' && Count(url, '(') < Count(url, ')')) ||
                    (last == ']' && Count(url, '[') < Count(url, ']')) ||
                    (last == '}' && Count(url, '{') < Count(url, '}')))
                {
                    url = url.Substring(0, url.Length - 1);
                    continue;
                }
                break;
            }
            return url;
        }

        private static int Count(string text, char c)
        {
            int n = 0;
            foreach (char ch in text) if (ch == c) n++;
            return n;
        }
    }
}
