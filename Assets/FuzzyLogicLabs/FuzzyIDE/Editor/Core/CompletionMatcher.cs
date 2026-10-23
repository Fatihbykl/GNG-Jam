using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>Ranks completion names against what was typed: exact, prefix, CamelCase humps ("gCo" = GetComponent), substring.</summary>
    public static class CompletionMatcher
    {
        public const int Exact = 1000, CasePrefix = 900, Prefix = 800, Humps = 700, Substring = 500;

        /// <summary>Higher is better; -1 when <paramref name="candidate"/> doesn't match.</summary>
        public static int Score(string candidate, string typed, bool camelCase = true)
        {
            if (string.IsNullOrEmpty(typed)) return 0;
            if (string.IsNullOrEmpty(candidate)) return -1;
            if (candidate == typed) return Exact;
            if (candidate.StartsWith(typed, StringComparison.Ordinal)) return CasePrefix;
            if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return Prefix;
            if (camelCase && typed.Length >= 2 && typed.Length <= 16 && MatchesHumps(candidate, typed)) return Humps;
            int index = candidate.IndexOf(typed, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? Substring - Math.Min(index, 100) : -1;
        }

        /// <summary>
        /// Every typed character matches in order, either continuing the current word or starting a later one
        /// (an uppercase letter, a digit run, or the character after '_'); the first must match the first character.
        /// </summary>
        public static bool MatchesHumps(string candidate, string typed)
        {
            if (!SameLetter(candidate[0], typed[0])) return false;
            return Match(candidate, typed, 1, 1);
        }

        private static bool Match(string c, string p, int ci, int pi)
        {
            if (pi == p.Length) return true;
            if (ci >= c.Length) return false;
            if (SameLetter(c[ci], p[pi]) && Match(c, p, ci + 1, pi + 1)) return true;
            for (int j = ci + 1; j < c.Length; j++)
            {
                if (IsHumpStart(c, j) && SameLetter(c[j], p[pi]) && Match(c, p, j + 1, pi + 1)) return true;
            }
            return false;
        }

        private static bool IsHumpStart(string s, int i)
        {
            char ch = s[i], prev = s[i - 1];
            if (prev == '_') return ch != '_';
            if (char.IsDigit(ch)) return !char.IsDigit(prev);
            if (!char.IsUpper(ch)) return false;
            // "HTTPServer": 'S' starts a word, the letters inside "HTTP" after the first don't.
            return !char.IsUpper(prev) || (i + 1 < s.Length && char.IsLower(s[i + 1]));
        }

        private static bool SameLetter(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
    }

    /// <summary>How often each completion was accepted; used to rank frequent picks first. Persisted in EditorPrefs.</summary>
    public static class CompletionHistory
    {
        private const string PrefsKey = "FuzzyIDE_CompletionHistory";
        private const int MaxEntries = 400;
        private static Dictionary<string, int> s_Counts;

        public static int GetCount(string name)
        {
            EnsureLoaded();
            return name != null && s_Counts.TryGetValue(name, out int count) ? count : 0;
        }

        public static void Record(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf(';') >= 0 || name.IndexOf('=') >= 0) return;
            EnsureLoaded();
            s_Counts.TryGetValue(name, out int count);
            s_Counts[name] = count + 1;
            if (s_Counts.Count > MaxEntries)
            {
                // Forget the least used half-and-a-bit so the list keeps adapting.
                foreach (var key in s_Counts.OrderBy(k => k.Value).Take(s_Counts.Count - MaxEntries * 3 / 4).Select(k => k.Key).ToList())
                    s_Counts.Remove(key);
            }
            EditorPrefs.SetString(PrefsKey, string.Join(";", s_Counts.Select(k => k.Key + "=" + k.Value)));
        }

        private static void EnsureLoaded()
        {
            if (s_Counts != null) return;
            s_Counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in EditorPrefs.GetString(PrefsKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = entry.LastIndexOf('=');
                if (eq > 0 && int.TryParse(entry.Substring(eq + 1), out int count)) s_Counts[entry.Substring(0, eq)] = count;
            }
        }
    }
}
