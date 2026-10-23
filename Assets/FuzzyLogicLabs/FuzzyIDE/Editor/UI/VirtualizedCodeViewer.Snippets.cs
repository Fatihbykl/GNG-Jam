using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>Snippet insertion with tab stops: Tab / Shift+Tab walk the placeholders, Esc or leaving the snippet ends it.</summary>
    public partial class VirtualizedCodeViewer
    {
        private sealed class SnippetSession
        {
            public List<Vector3Int> Stops; // x = line, y = column, z = placeholder length
            public int Current;
            public int LineCount;
            public int FirstLine, LastLine;
            public int ActiveLineLength;
        }

        private SnippetSession m_Snippet;

        /// <summary>Inserts <paramref name="body"/> at (line, col) after <paramref name="leadingCommands"/>, as one undo step.</summary>
        private void InsertSnippet(string body, int line, int col, List<IEditCommand> leadingCommands = null)
        {
            string lineText = m_Lines[line];
            string indent = new string(lineText.TakeWhile(char.IsWhiteSpace).ToArray());
            var expanded = SnippetParser.Expand(body, indent, CodeIDESettings.Current.Editor.TabWidth);

            var cmds = leadingCommands ?? new List<IEditCommand>();
            cmds.Add(new InsertTextCommand(m_Lines, expanded.Text, line, col, SetCaretAction));
            ExecuteHistory(cmds);

            var stops = new List<Vector3Int>();
            foreach (var stop in expanded.Stops)
            {
                var pos = OffsetToPosition(expanded.Text, stop.Offset, line, col);
                stops.Add(new Vector3Int(pos.x, pos.y, stop.Length));
            }

            int lastLine = line + expanded.Text.Count(c => c == '\n');
            m_Snippet = stops.Count > 1
                ? new SnippetSession { Stops = stops, LineCount = m_Lines.Count, FirstLine = line, LastLine = lastLine }
                : null;

            if (m_Snippet != null) ActivateSnippetStop(0);
            else SetCaretPosition(stops[0].x, stops[0].y, clearSelection: true);
            RefreshListView();
        }

        private static Vector2Int OffsetToPosition(string text, int offset, int line, int col)
        {
            int l = line, c = col;
            for (int i = 0; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\n') { l++; c = 0; }
                else c++;
            }
            return new Vector2Int(l, c);
        }

        private void ActivateSnippetStop(int index)
        {
            var session = m_Snippet;
            session.Current = index;
            var stop = session.Stops[index];
            int lineLength = m_Lines[stop.x].Length;
            int start = Mathf.Clamp(stop.y, 0, lineLength);
            int end = Mathf.Clamp(stop.y + stop.z, start, lineLength);
            session.ActiveLineLength = lineLength;

            bool select = end > start;
            m_AnchorLine = stop.x;
            m_AnchorColumn = start;
            SetCaretPosition(stop.x, end, clearSelection: !select);
            m_HasSelection = select;
            UpdateCaretPosition();

            // $0 is the final position: the session is over once the caret lands there.
            if (index == session.Stops.Count - 1) m_Snippet = null;
        }

        /// <summary>Moves to the next/previous tab stop; false when no snippet is active (Tab then indents as usual).</summary>
        private bool TryAdvanceSnippet(bool backwards)
        {
            var session = m_Snippet;
            if (session == null) return false;

            // Typing Enter inside a placeholder or moving the caret out of the snippet ends the session.
            if (m_Lines.Count != session.LineCount || m_CaretLine < session.FirstLine || m_CaretLine > session.LastLine)
            {
                m_Snippet = null;
                return false;
            }

            // Text typed into the active placeholder shifts the stops after it on the same line.
            var current = session.Stops[session.Current];
            int delta = m_Lines[current.x].Length - session.ActiveLineLength;
            if (delta != 0)
            {
                for (int i = 0; i < session.Stops.Count; i++)
                {
                    var s = session.Stops[i];
                    if (i != session.Current && s.x == current.x && s.y > current.y) session.Stops[i] = new Vector3Int(s.x, s.y + delta, s.z);
                }
            }

            int next = session.Current + (backwards ? -1 : 1);
            if (next < 0) next = 0;
            ActivateSnippetStop(Mathf.Min(next, session.Stops.Count - 1));
            return true;
        }

        private void EndSnippetSession()
        {
            m_Snippet = null;
        }
    }
}
