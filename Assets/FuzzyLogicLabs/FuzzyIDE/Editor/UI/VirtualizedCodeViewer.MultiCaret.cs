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
        private void AddExtraCaretDown()
        {
            if (m_AutoCompletePopup != null && m_AutoCompletePopup.IsVisible)
            {
                m_AutoCompletePopup.Hide();
            }

            int refLine = m_CaretLine;
            int refCol = m_CaretColumn;
            if (m_ExtraCarets.Count > 0)
            {
                var last = m_ExtraCarets[m_ExtraCarets.Count - 1];
                refLine = last.Line;
                refCol = last.Column;
            }

            int nextLine = refLine + 1;
            if (nextLine < m_Lines.Count)
            {
                int safeCol = Mathf.Min(refCol, m_Lines[nextLine].Length);
                m_ExtraCarets.Add(new CaretPos(nextLine, safeCol));
                RefreshListView();
                UpdateCaretPosition();
            }
        }

        private void AddExtraCaretUp()
        {
            if (m_AutoCompletePopup != null && m_AutoCompletePopup.IsVisible)
            {
                m_AutoCompletePopup.Hide();
            }

            int refLine = m_CaretLine;
            int refCol = m_CaretColumn;
            if (m_ExtraCarets.Count > 0)
            {
                var first = m_ExtraCarets[m_ExtraCarets.Count - 1];
                refLine = first.Line;
                refCol = first.Column;
            }

            int prevLine = refLine - 1;
            if (prevLine >= 0)
            {
                int safeCol = Mathf.Min(refCol, m_Lines[prevLine].Length);
                m_ExtraCarets.Add(new CaretPos(prevLine, safeCol));
                RefreshListView();
                UpdateCaretPosition();
            }
        }

        public void ClearExtraCarets()
        {
            if (m_ExtraCarets.Count > 0)
            {
                m_ExtraCarets.Clear();
                foreach (var overlay in m_ExtraCaretOverlays)
                {
                    overlay.style.display = DisplayStyle.None;
                }
                RefreshListView();
                UpdateCaretPosition();
            }
        }

        private bool IsExtraCaretLine(int lineIndex)
        {
            if (m_ExtraCarets == null || m_ExtraCarets.Count == 0) return false;
            for (int i = 0; i < m_ExtraCarets.Count; i++)
            {
                if (m_ExtraCarets[i].Line == lineIndex) return true;
            }
            return false;
        }

        private void UpdateExtraCaretPositions(ScrollView scrollView)
        {
            while (m_ExtraCaretOverlays.Count < m_ExtraCarets.Count)
            {
                var overlay = new VisualElement();
                overlay.name = "extra-caret-overlay";
                overlay.style.position = Position.Absolute;
                overlay.style.width = 2;
                overlay.style.height = LINE_HEIGHT;
                overlay.style.backgroundColor = new Color(0.9f, 0.9f, 0.9f, 1f);
                overlay.pickingMode = PickingMode.Ignore;
                Add(overlay);
                m_ExtraCaretOverlays.Add(overlay);
            }

            for (int i = 0; i < m_ExtraCaretOverlays.Count; i++)
            {
                if (i < m_ExtraCarets.Count)
                {
                    var pos = m_ExtraCarets[i];
                    int visIdx = GetVisibleLineIndex(pos.Line);
                    if (visIdx != -1 && scrollView != null)
                    {
                        m_ExtraCaretOverlays[i].style.display = DisplayStyle.Flex;
                        float cy = (visIdx * LINE_HEIGHT) - scrollView.scrollOffset.y;
                        int safeCol = Mathf.Clamp(pos.Column, 0, m_Lines[pos.Line].Length);
                        float cx = LINE_NUM_WIDTH + CODE_PADDING_LEFT + GetTextWidth(pos.Line, safeCol) - scrollView.scrollOffset.x;
                        m_ExtraCaretOverlays[i].style.top = cy;
                        m_ExtraCaretOverlays[i].style.left = cx;
                    }
                    else
                    {
                        m_ExtraCaretOverlays[i].style.display = DisplayStyle.None;
                    }
                }
                else
                {
                    m_ExtraCaretOverlays[i].style.display = DisplayStyle.None;
                }
            }
        }

        private void MultiInsertText(string text)
        {
            var allCarets = new List<CaretPos>();
            allCarets.Add(new CaretPos(m_CaretLine, m_CaretColumn));
            allCarets.AddRange(m_ExtraCarets);
            allCarets.Sort((a, b) =>
            {
                int lineCmp = b.Line.CompareTo(a.Line);
                return lineCmp != 0 ? lineCmp : b.Column.CompareTo(a.Column);
            });

            var cmds = new List<IEditCommand>();
            for (int i = 0; i < allCarets.Count; i++)
            {
                var cp = allCarets[i];
                cmds.Add(new InsertTextCommand(m_Lines, text, cp.Line, cp.Column, (r, c) => { }));
            }
            ExecuteHistory(cmds);

            m_CaretColumn += text.Length;
            for (int i = 0; i < m_ExtraCarets.Count; i++)
            {
                var cp = m_ExtraCarets[i];
                m_ExtraCarets[i] = new CaretPos(cp.Line, cp.Column + text.Length);
            }
            RefreshListView();
            UpdateCaretPosition();
        }

        private void MultiBackspace()
        {
            var allCarets = new List<CaretPos>();
            allCarets.Add(new CaretPos(m_CaretLine, m_CaretColumn));
            allCarets.AddRange(m_ExtraCarets);
            allCarets.Sort((a, b) =>
            {
                int lineCmp = b.Line.CompareTo(a.Line);
                return lineCmp != 0 ? lineCmp : b.Column.CompareTo(a.Column);
            });

            var cmds = new List<IEditCommand>();
            for (int i = 0; i < allCarets.Count; i++)
            {
                var cp = allCarets[i];
                if (cp.Column > 0)
                {
                    string lineText = m_Lines[cp.Line];
                    string delChar = lineText.Substring(cp.Column - 1, 1);
                    cmds.Add(new DeleteTextCommand(m_Lines, delChar, cp.Line, cp.Column - 1, false, (r, c) => { }));
                }
            }
            if (cmds.Count > 0)
            {
                ExecuteHistory(cmds);
                if (m_CaretColumn > 0) m_CaretColumn--;
                for (int i = 0; i < m_ExtraCarets.Count; i++)
                {
                    var cp = m_ExtraCarets[i];
                    if (cp.Column > 0) m_ExtraCarets[i] = new CaretPos(cp.Line, cp.Column - 1);
                }
                RefreshListView();
                UpdateCaretPosition();
            }
        }

        private void MultiDelete()
        {
            var allCarets = new List<CaretPos>();
            allCarets.Add(new CaretPos(m_CaretLine, m_CaretColumn));
            allCarets.AddRange(m_ExtraCarets);
            allCarets.Sort((a, b) =>
            {
                int lineCmp = b.Line.CompareTo(a.Line);
                return lineCmp != 0 ? lineCmp : b.Column.CompareTo(a.Column);
            });

            var cmds = new List<IEditCommand>();
            for (int i = 0; i < allCarets.Count; i++)
            {
                var cp = allCarets[i];
                if (cp.Column < m_Lines[cp.Line].Length)
                {
                    string lineText = m_Lines[cp.Line];
                    string delChar = lineText.Substring(cp.Column, 1);
                    cmds.Add(new DeleteTextCommand(m_Lines, delChar, cp.Line, cp.Column, false, (r, c) => { }));
                }
            }
            if (cmds.Count > 0)
            {
                ExecuteHistory(cmds);
                RefreshListView();
                UpdateCaretPosition();
            }
        }

        private void MultiMoveCaretLeft(bool shift)
        {
            MoveCaretLeft(shift);
            for (int i = 0; i < m_ExtraCarets.Count; i++)
            {
                var cp = m_ExtraCarets[i];
                if (cp.Column > 0)
                {
                    m_ExtraCarets[i] = new CaretPos(cp.Line, cp.Column - 1);
                }
                else if (cp.Line > 0)
                {
                    m_ExtraCarets[i] = new CaretPos(cp.Line - 1, m_Lines[cp.Line - 1].Length);
                }
            }
            RefreshListView();
            UpdateCaretPosition();
        }

        private void MultiMoveCaretRight(bool shift)
        {
            MoveCaretRight(shift);
            for (int i = 0; i < m_ExtraCarets.Count; i++)
            {
                var cp = m_ExtraCarets[i];
                if (cp.Column < m_Lines[cp.Line].Length)
                {
                    m_ExtraCarets[i] = new CaretPos(cp.Line, cp.Column + 1);
                }
                else if (cp.Line < m_Lines.Count - 1)
                {
                    m_ExtraCarets[i] = new CaretPos(cp.Line + 1, 0);
                }
            }
            RefreshListView();
            UpdateCaretPosition();
        }
    }
}
