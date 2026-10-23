using System;
using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualizedCodeViewer
    {
        // -----------------------
        // Keyboard Event Handling
        // -----------------------
        public void OpenFindPanel()
        {
            m_FindReplacePanel.style.display = DisplayStyle.Flex;
            if (m_HasSelection)
            {
                string selected = GetSelectedText();
                if (!string.IsNullOrEmpty(selected))
                {
                    string firstLine = selected.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(firstLine))
                    {
                        m_FindReplacePanel.SetFindText(firstLine);
                        return;
                    }
                }
            }
            m_FindReplacePanel.FocusFind();
        }

        // -----------------------
        // Find & Replace
        // -----------------------
        private void ClearSearch()
        {
            RefreshScrollMarkers();
            m_SearchMatches.Clear();
            m_ActiveSearchMatchIndex = -1;
            UpdateSearchVisuals();
        }

        private void PerformSearch(string query, bool matchCase, bool autoJump = true)
        {
            schedule.Execute(RefreshScrollMarkers);
            m_SearchMatches.Clear();
            m_ActiveSearchMatchIndex = -1;
            if (string.IsNullOrEmpty(query))
            {
                UpdateSearchVisuals();
                return;
            }

            StringComparison comp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            for (int l = 0; l < m_Lines.Count; l++)
            {
                string line = m_Lines[l];
                int index = 0;
                while (index < line.Length)
                {
                    int found = line.IndexOf(query, index, comp);
                    if (found >= 0)
                    {
                        m_SearchMatches.Add(new SearchMatch { Line = l, Col = found, Length = query.Length });
                        index = found + query.Length;
                    }
                    else break;
                }
            }

            if (m_SearchMatches.Count > 0 && autoJump)
            {
                m_ActiveSearchMatchIndex = 0;
                JumpToLine(m_SearchMatches[0].Line, takeFocus: false);
            }
        
            if (m_FindReplacePanel != null)
            {
                m_FindReplacePanel.UpdateMatchCount(m_SearchMatches.Count > 0 ? m_ActiveSearchMatchIndex + 1 : 0, m_SearchMatches.Count);
            }
            UpdateSearchVisuals();
        }

        private void NavigateSearch(int direction)
        {
            if (m_SearchMatches.Count == 0) return;
            m_ActiveSearchMatchIndex += direction;
            if (m_ActiveSearchMatchIndex >= m_SearchMatches.Count) m_ActiveSearchMatchIndex = 0;
            if (m_ActiveSearchMatchIndex < 0) m_ActiveSearchMatchIndex = m_SearchMatches.Count - 1;

            if (m_FindReplacePanel != null)
            {
                m_FindReplacePanel.UpdateMatchCount(m_ActiveSearchMatchIndex + 1, m_SearchMatches.Count);
            }

            JumpToLine(m_SearchMatches[m_ActiveSearchMatchIndex].Line, takeFocus: false);
            UpdateSearchVisuals();
        }

        private void ReplaceActiveMatch(string findText, string replaceText, bool matchCase)
        {
            if (m_ActiveSearchMatchIndex < 0 || m_ActiveSearchMatchIndex >= m_SearchMatches.Count) return;

            var match = m_SearchMatches[m_ActiveSearchMatchIndex];

            int safeCol = Mathf.Clamp(match.Col, 0, m_Lines[match.Line].Length);
            int safeLen = Mathf.Min(match.Length, m_Lines[match.Line].Length - safeCol);

            var delCmd = new DeleteTextCommand(m_Lines, m_Lines[match.Line].Substring(safeCol, safeLen), match.Line, safeCol, false, SetCaretAction);
            var insCmd = new InsertTextCommand(m_Lines, replaceText, match.Line, safeCol, SetCaretAction);
            ExecuteHistory(new List<IEditCommand> { delCmd, insCmd });

            PerformSearch(findText, matchCase, autoJump: false);
        }

        private void ReplaceAllMatches(string findText, string replaceText, bool matchCase)
        {
            if (m_SearchMatches.Count == 0) return;

            var cmds = new List<IEditCommand>();

            for (int i = m_SearchMatches.Count - 1; i >= 0; i--)
            {
                var match = m_SearchMatches[i];
                int safeCol = Mathf.Clamp(match.Col, 0, m_Lines[match.Line].Length);
                int safeLen = Mathf.Min(match.Length, m_Lines[match.Line].Length - safeCol);

                cmds.Add(new DeleteTextCommand(m_Lines, m_Lines[match.Line].Substring(safeCol, safeLen), match.Line, safeCol, false, SetCaretAction));
                cmds.Add(new InsertTextCommand(m_Lines, replaceText, match.Line, safeCol, SetCaretAction));
            }

            ExecuteHistory(cmds);
            PerformSearch(findText, matchCase, autoJump: false);
        }

        private void UpdateSearchVisuals()
        {
            foreach (var rect in m_SearchRects) rect.style.display = DisplayStyle.None;
            if (m_SearchMatches.Count == 0) return;

            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;
            Vector2 scroll = scrollView.scrollOffset;

            int firstVisibleLine = Mathf.Max(0, (int)(scroll.y / LINE_HEIGHT));
            int lastVisibleLine = Mathf.Min(m_VisibleLines.Count - 1, firstVisibleLine + (int)(scrollView.layout.height / LINE_HEIGHT) + 1);

            int rectIndex = 0;
            for (int i = 0; i < m_SearchMatches.Count; i++)
            {
                var match = m_SearchMatches[i];
                int visIdx = GetVisibleLineIndex(match.Line);
                if (visIdx == -1 || visIdx < firstVisibleLine || visIdx > lastVisibleLine) continue;

                float yPos = (visIdx * LINE_HEIGHT) - scroll.y;

                int safeCol = Mathf.Clamp(match.Col, 0, m_Lines[match.Line].Length);
                int safeEnd = Mathf.Clamp(match.Col + match.Length, safeCol, m_Lines[match.Line].Length);

                float startWidth = GetTextWidth(match.Line, safeCol);
                float endWidth = GetTextWidth(match.Line, safeEnd);

                float xPos = LINE_NUM_WIDTH + CODE_PADDING_LEFT + startWidth - scroll.x;

                VisualElement rect = GetOrCreateSearchRect(rectIndex++);
                rect.style.top = yPos;
                rect.style.left = xPos;
                rect.style.width = endWidth - startWidth;
                rect.style.height = LINE_HEIGHT;

                if (i == m_ActiveSearchMatchIndex)
                    rect.style.backgroundColor = new Color(1f, 0.5f, 0f, 0.6f);
                else
                    rect.style.backgroundColor = new Color(1f, 1f, 0f, 0.3f);

                rect.style.display = DisplayStyle.Flex;
            }
        }

        private VisualElement GetOrCreateSearchRect(int index)
        {
            while (m_SearchRects.Count <= index)
            {
                var rect = new VisualElement();
                rect.style.position = Position.Absolute;
                rect.pickingMode = PickingMode.Ignore;
                m_SearchContainer.Add(rect);
                m_SearchRects.Add(rect);
            }
            return m_SearchRects[index];
        }
    }
}
