using System;
using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Git
{
    public static class GitDiffEngine
    {
        private enum EditType { Keep, Insert, Delete }

        private struct Edit
        {
            public EditType Type;
            public int OldIndex;
            public int NewIndex;
        }

        public static GitLineDiffResult ComputeDiff(IReadOnlyList<string> headLines, IReadOnlyList<string> currentLines)
        {
            var result = new GitLineDiffResult();

            if (currentLines == null || currentLines.Count == 0)
            {
                if (headLines != null && headLines.Count > 0)
                {
                    result.DeletedCount = headLines.Count;
                }
                return result;
            }

            if (headLines == null || headLines.Count == 0)
            {
                // New file or untracked: all lines added
                for (int i = 0; i < currentLines.Count; i++)
                {
                    result.LineStatuses[i] = GitLineDiffType.Added;
                }
                result.AddedCount = currentLines.Count;
                return result;
            }

            int n = headLines.Count;
            int m = currentLines.Count;

            // Fast path: Exact same count and contents
            if (n == m)
            {
                bool identical = true;
                for (int i = 0; i < n; i++)
                {
                    if (!string.Equals(headLines[i], currentLines[i], StringComparison.Ordinal))
                    {
                        identical = false;
                        break;
                    }
                }
                if (identical) return result;
            }

            // Trim common prefix
            int prefix = 0;
            while (prefix < n && prefix < m && string.Equals(headLines[prefix], currentLines[prefix], StringComparison.Ordinal))
            {
                prefix++;
            }

            // Trim common suffix
            int suffix = 0;
            while (suffix < (n - prefix) && suffix < (m - prefix) &&
                   string.Equals(headLines[n - 1 - suffix], currentLines[m - 1 - suffix], StringComparison.Ordinal))
            {
                suffix++;
            }

            int trimmedN = n - prefix - suffix;
            int trimmedM = m - prefix - suffix;

            var edits = new List<Edit>();

            if (trimmedN > 0 || trimmedM > 0)
            {
                RunMyersDiff(headLines, prefix, trimmedN, currentLines, prefix, trimmedM, edits);
            }

            // Analyze edits to categorize into Added, Modified, and DeletedAbove
            int eIdx = 0;
            while (eIdx < edits.Count)
            {
                var cur = edits[eIdx];

                if (cur.Type == EditType.Delete)
                {
                    // Collect consecutive deletes
                    var deletedLines = new List<string>();
                    while (eIdx < edits.Count && edits[eIdx].Type == EditType.Delete)
                    {
                        deletedLines.Add(headLines[edits[eIdx].OldIndex]);
                        eIdx++;
                    }

                    // Check if followed by consecutive inserts (replacement/modification)
                    var insertedIndices = new List<int>();
                    while (eIdx < edits.Count && edits[eIdx].Type == EditType.Insert)
                    {
                        insertedIndices.Add(edits[eIdx].NewIndex);
                        eIdx++;
                    }

                    int delCount = deletedLines.Count;
                    int insCount = insertedIndices.Count;

                    if (insCount > 0)
                    {
                        // Modified lines: overlap between deleted and inserted
                        int modCount = Math.Min(delCount, insCount);
                        for (int i = 0; i < modCount; i++)
                        {
                            result.LineStatuses[insertedIndices[i]] = GitLineDiffType.Modified;
                            result.ModifiedCount++;
                        }

                        // Excess inserts are Added lines
                        for (int i = modCount; i < insCount; i++)
                        {
                            result.LineStatuses[insertedIndices[i]] = GitLineDiffType.Added;
                            result.AddedCount++;
                        }

                        // Excess deletes: mark on the first inserted line
                        if (delCount > insCount)
                        {
                            int targetLine = insertedIndices[0];
                            if (!result.DeletedLinesAbove.TryGetValue(targetLine, out var list))
                            {
                                list = new List<string>();
                                result.DeletedLinesAbove[targetLine] = list;
                            }
                            for (int i = insCount; i < delCount; i++)
                            {
                                list.Add(deletedLines[i]);
                            }
                            result.DeletedCount += (delCount - insCount);
                        }
                    }
                    else
                    {
                        // Pure deletion with no insertion
                        int targetLine = cur.NewIndex;
                        if (targetLine >= m) targetLine = m - 1;
                        if (targetLine < 0) targetLine = 0;

                        if (!result.DeletedLinesAbove.TryGetValue(targetLine, out var list))
                        {
                            list = new List<string>();
                            result.DeletedLinesAbove[targetLine] = list;
                        }
                        list.AddRange(deletedLines);

                        if (!result.LineStatuses.ContainsKey(targetLine))
                        {
                            result.LineStatuses[targetLine] = GitLineDiffType.DeletedAbove;
                        }
                        result.DeletedCount += delCount;
                    }
                }
                else if (cur.Type == EditType.Insert)
                {
                    result.LineStatuses[cur.NewIndex] = GitLineDiffType.Added;
                    result.AddedCount++;
                    eIdx++;
                }
                else
                {
                    eIdx++;
                }
            }

            return result;
        }

        private static void RunMyersDiff(
            IReadOnlyList<string> a, int aOffset, int aLen,
            IReadOnlyList<string> b, int bOffset, int bLen,
            List<Edit> result)
        {
            if (aLen == 0)
            {
                for (int i = 0; i < bLen; i++)
                {
                    result.Add(new Edit { Type = EditType.Insert, OldIndex = aOffset, NewIndex = bOffset + i });
                }
                return;
            }
            if (bLen == 0)
            {
                for (int i = 0; i < aLen; i++)
                {
                    result.Add(new Edit { Type = EditType.Delete, OldIndex = aOffset + i, NewIndex = bOffset });
                }
                return;
            }

            int max = aLen + bLen;
            int vOffset = max;
            int[] v = new int[2 * max + 1];
            v[vOffset + 1] = 0;

            var trace = new List<int[]>();

            for (int d = 0; d <= max; d++)
            {
                int[] vCopy = (int[])v.Clone();
                trace.Add(vCopy);

                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[vOffset + k - 1] < v[vOffset + k + 1]))
                    {
                        x = v[vOffset + k + 1];
                    }
                    else
                    {
                        x = v[vOffset + k - 1] + 1;
                    }

                    int y = x - k;

                    while (x < aLen && y < bLen && string.Equals(a[aOffset + x], b[bOffset + y], StringComparison.Ordinal))
                    {
                        x++;
                        y++;
                    }

                    v[vOffset + k] = x;

                    if (x >= aLen && y >= bLen)
                    {
                        // Backtrack
                        Backtrack(trace, aOffset, aLen, bOffset, bLen, max, result);
                        return;
                    }
                }
            }
        }

        private static void Backtrack(
            List<int[]> trace, int aOffset, int aLen, int bOffset, int bLen, int max,
            List<Edit> result)
        {
            int x = aLen;
            int y = bLen;

            var reversedEdits = new List<Edit>();

            for (int d = trace.Count - 1; d >= 0; d--)
            {
                int[] v = trace[d];
                int k = x - y;

                int prevK;
                if (k == -d || (k != d && v[max + k - 1] < v[max + k + 1]))
                {
                    prevK = k + 1;
                }
                else
                {
                    prevK = k - 1;
                }

                int prevX = v[max + prevK];
                int prevY = prevX - prevK;

                while (x > prevX && y > prevY)
                {
                    // Diagonal (Keep)
                    x--;
                    y--;
                }

                if (d > 0)
                {
                    if (x == prevX)
                    {
                        // Vertical: Insert
                        reversedEdits.Add(new Edit { Type = EditType.Insert, OldIndex = aOffset + x, NewIndex = bOffset + prevY });
                    }
                    else
                    {
                        // Horizontal: Delete
                        reversedEdits.Add(new Edit { Type = EditType.Delete, OldIndex = aOffset + prevX, NewIndex = bOffset + y });
                    }
                }

                x = prevX;
                y = prevY;
            }

            for (int i = reversedEdits.Count - 1; i >= 0; i--)
            {
                result.Add(reversedEdits[i]);
            }
        }
    }
}
