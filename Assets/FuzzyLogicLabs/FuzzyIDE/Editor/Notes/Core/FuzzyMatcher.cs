using System;
using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>
    /// Subsequence fuzzy matcher for the quick switcher and command palette (VS Code / Obsidian style).
    /// Every query character must appear in order. Prefixes score highest, then word-start/acronym
    /// matches ("pm" → "Player Motor"), then contiguous text inside a word, then scattered characters.
    /// </summary>
    public static class FuzzyMatcher
    {
        public const int NoMatch = int.MinValue;

        /// <summary>Score of <paramref name="candidate"/> for <paramref name="query"/> (higher is better) or <see cref="NoMatch"/>.</summary>
        /// <param name="matches">Optional output: indices of matched characters in the candidate.</param>
        public static int Score(string query, string candidate, List<int> matches = null)
        {
            matches?.Clear();
            if (string.IsNullOrEmpty(query)) return 0;
            if (string.IsNullOrEmpty(candidate)) return NoMatch;

            query = query.Trim();
            if (query.Length == 0) return 0;

            // Contiguous occurrence: great at the start or at a word start, modest inside a word.
            int substringScore = NoMatch;
            int sub = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (sub >= 0)
            {
                substringScore = 30 + query.Length * 4 - Math.Min(sub, 10);
                if (sub == 0) substringScore += 40;
                else if (IsWordStart(candidate, sub)) substringScore += 25;
                if (query.Length == candidate.Length) substringScore += 20;
            }

            var greedyMatches = new List<int>();
            int greedyScore = Greedy(query, candidate, greedyMatches);

            if (substringScore == NoMatch && greedyScore == NoMatch) return NoMatch;

            if (substringScore >= greedyScore)
            {
                if (matches != null) for (int i = 0; i < query.Length; i++) matches.Add(sub + i);
                return substringScore;
            }

            matches?.AddRange(greedyMatches);
            return greedyScore;
        }

        /// <summary>Left-to-right match preferring nearby word starts; rewards runs and word starts, penalizes gaps.</summary>
        private static int Greedy(string query, string candidate, List<int> picked)
        {
            int total = 0;
            int prev = -1;
            int ci = 0;

            for (int qi = 0; qi < query.Length; qi++)
            {
                char q = char.ToLowerInvariant(query[qi]);
                if (char.IsWhiteSpace(q)) continue;

                int first = -1, wordStart = -1;
                for (int i = ci; i < candidate.Length; i++)
                {
                    if (char.ToLowerInvariant(candidate[i]) != q) continue;
                    if (first < 0) first = i;
                    if (IsWordStart(candidate, i)) { wordStart = i; break; }
                    if (i - first > 12) break;
                }
                if (first < 0) return NoMatch;

                // Keep a contiguous run going; otherwise jump to the next word start when there is one.
                int at = first == prev + 1 || wordStart < 0 ? first : wordStart;
                picked.Add(at);

                int charScore = 1;
                if (at == prev + 1) charScore += 6;               // contiguous
                if (IsWordStart(candidate, at)) charScore += 15;  // word / camel-case start (acronyms)
                if (at == 0) charScore += 15;                     // candidate start
                int gap = prev < 0 ? at : at - prev - 1;
                charScore -= Math.Min(gap, prev < 0 ? 10 : 4);

                total += charScore;
                prev = at;
                ci = at + 1;
            }

            return total;
        }

        private static bool IsWordStart(string s, int i)
        {
            if (i <= 0) return true;
            char c = s[i];
            char p = s[i - 1];
            if (!char.IsLetterOrDigit(p)) return true;
            return char.IsUpper(c) && char.IsLower(p);
        }
    }
}
