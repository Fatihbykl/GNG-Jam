using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    public enum SearchRowKind { Note, Match, More }

    public enum SearchSort { Relevance, Modified, TitleAscending, TitleDescending }

    /// <summary>One line of the search pane: a note header, one of its matching lines, or "N more".</summary>
    public sealed class SearchRow
    {
        public SearchRowKind Kind;
        public NoteSearchResult Result;
        public NoteSearchMatch Match;
        /// <summary>Hidden matches for <see cref="SearchRowKind.More"/>.</summary>
        public int Count;
    }

    /// <summary>Turns search results into the flat, collapsible row list of the search pane (pure logic).</summary>
    public static class SearchRows
    {
        public static List<SearchRow> Build(IEnumerable<NoteSearchResult> results, Func<NoteSearchResult, bool> isCollapsed)
        {
            var rows = new List<SearchRow>();
            foreach (var r in results)
            {
                rows.Add(new SearchRow { Kind = SearchRowKind.Note, Result = r });
                if (isCollapsed != null && isCollapsed(r)) continue;
                foreach (var m in r.Matches) rows.Add(new SearchRow { Kind = SearchRowKind.Match, Result = r, Match = m });
                if (r.MoreMatches > 0) rows.Add(new SearchRow { Kind = SearchRowKind.More, Result = r, Count = r.MoreMatches });
            }
            return rows;
        }

        public static List<NoteSearchResult> Sort(IEnumerable<NoteSearchResult> results, SearchSort sort)
        {
            switch (sort)
            {
                case SearchSort.Modified:
                    return results.OrderByDescending(r => r.Note.UpdatedAt).ToList();
                case SearchSort.TitleAscending:
                    return results.OrderBy(r => r.Note.Title, StringComparer.OrdinalIgnoreCase).ToList();
                case SearchSort.TitleDescending:
                    return results.OrderByDescending(r => r.Note.Title, StringComparer.OrdinalIgnoreCase).ToList();
                default:
                    return results.ToList(); // NoteSearch.Run already ranks by relevance
            }
        }

        public static int TotalMatches(IEnumerable<NoteSearchResult> results) => results.Sum(r => r.Matches.Count + r.MoreMatches);

        /// <summary>Markdown list of links to the found notes ("Copy search results").</summary>
        public static string ToMarkdown(IEnumerable<NoteSearchResult> results)
        {
            var sb = new StringBuilder();
            foreach (var r in results) sb.Append("- [[").Append(r.Note.LinkName).Append("]]\n");
            return sb.ToString();
        }
    }
}
