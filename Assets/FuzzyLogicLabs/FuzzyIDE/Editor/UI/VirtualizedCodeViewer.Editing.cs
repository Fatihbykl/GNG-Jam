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
        public void ExecuteHistory(List<IEditCommand> cmds)
        {
            if (cmds.Count == 0) return;
            IEditCommand finalCmd;
            if (cmds.Count == 1) finalCmd = cmds[0];
            else finalCmd = new CompositeCommand(cmds);

            int oldLineCount = m_Lines.Count;
            int oldCaretLine = m_CaretLine;

            finalCmd.Execute(); // Mutates m_Lines and calls SetCaretAction
            m_History.AddCommand(finalCmd);

            OnTextChanged?.Invoke();
        
            RecalculateLiveBadges();

            if (m_FindReplacePanel.style.display == DisplayStyle.Flex)
            {
                PerformSearch(m_FindReplacePanel.GetFindText(), m_FindReplacePanel.GetMatchCase(), autoJump: false);
            }

            if (m_Lines.Count == oldLineCount && m_CaretLine == oldCaretLine)
            {
                // In-place single-line edit: line layout hasn't changed.
                // Avoid tearing down and rebuilding the entire ListView.
                int visIdx = GetVisibleLineIndex(m_CaretLine);
                if (visIdx >= 0) m_ListView.RefreshItem(visIdx);
                UpdateCaretPosition();
                TriggerCodeAnalysis();
            }
            else
            {
                RefreshListView();
            }
        }

        private DeleteSelectionCommand CreateDeleteSelectionCommand()
        {
            if (!m_HasSelection) return null;
            int startL = m_AnchorLine; int startC = m_AnchorColumn;
            int endL = m_CaretLine; int endC = m_CaretColumn;
            if (startL > endL || (startL == endL && startC > endC))
            {
                startL = m_CaretLine; startC = m_CaretColumn;
                endL = m_AnchorLine; endC = m_AnchorColumn;
            }
            return new DeleteSelectionCommand(m_Lines, startL, startC, endL, endC, SetCaretAction);
        }

        private void InsertText(string text)
        {
            var cmds = new List<IEditCommand>();
            var delCmd = CreateDeleteSelectionCommand();
            if (delCmd != null) cmds.Add(delCmd);

            int insL = m_CaretLine;
            int insC = m_CaretColumn;
            if (delCmd != null)
            {
                int startL = m_AnchorLine; int startC = m_AnchorColumn;
                int endL = m_CaretLine; int endC = m_CaretColumn;
                if (startL > endL || (startL == endL && startC > endC))
                {
                    startL = m_CaretLine; startC = m_CaretColumn;
                }
                insL = startL;
                insC = startC;
            }

            var insCmd = new InsertTextCommand(m_Lines, text, insL, insC, SetCaretAction);
            cmds.Add(insCmd);

            ExecuteHistory(cmds);
        }

        private void Backspace(bool word = false)
        {
            if (m_HasSelection)
            {
                ExecuteHistory(new List<IEditCommand> { CreateDeleteSelectionCommand() });
                return;
            }

            if (m_CaretColumn > 0)
            {
                if (word)
                {
                    int newCol = GetPreviousWordBoundary(m_CaretLine, m_CaretColumn);
                    string deletedStr = m_Lines[m_CaretLine].Substring(newCol, m_CaretColumn - newCol);
                    var delCmd = new DeleteTextCommand(m_Lines, deletedStr, m_CaretLine, newCol, false, SetCaretAction);
                    ExecuteHistory(new List<IEditCommand> { delCmd });
                }
                else
                {
                    string curLine = m_Lines[m_CaretLine];
                    if (m_CaretColumn < curLine.Length && m_CaretColumn > 0)
                    {
                        char prev = curLine[m_CaretColumn - 1];
                        char next = curLine[m_CaretColumn];
                        if ((prev == '(' && next == ')') || (prev == '{' && next == '}') || (prev == '[' && next == ']') || (prev == '"' && next == '"') || (prev == '\'' && next == '\''))
                        {
                            var pairDelCmd = new DeleteSelectionCommand(m_Lines, m_CaretLine, m_CaretColumn - 1, m_CaretLine, m_CaretColumn + 1, SetCaretAction);
                            ExecuteHistory(new List<IEditCommand> { pairDelCmd });
                            return;
                        }
                    }
                    string deletedChar = m_Lines[m_CaretLine].Substring(m_CaretColumn - 1, 1);
                    var delCmd = new DeleteTextCommand(m_Lines, deletedChar, m_CaretLine, m_CaretColumn - 1, false, SetCaretAction);
                    ExecuteHistory(new List<IEditCommand> { delCmd });
                }
            }
            else if (m_CaretLine > 0)
            {
                var delCmd = new DeleteTextCommand(m_Lines, "\n", m_CaretLine - 1, m_Lines[m_CaretLine - 1].Length, true, SetCaretAction);
                ExecuteHistory(new List<IEditCommand> { delCmd });
            }
        }

        private void Delete(bool word = false)
        {
            if (m_HasSelection)
            {
                ExecuteHistory(new List<IEditCommand> { CreateDeleteSelectionCommand() });
                return;
            }

            if (m_CaretColumn < m_Lines[m_CaretLine].Length)
            {
                if (word)
                {
                    int newCol = GetNextWordBoundary(m_CaretLine, m_CaretColumn);
                    string deletedStr = m_Lines[m_CaretLine].Substring(m_CaretColumn, newCol - m_CaretColumn);
                    var delCmd = new DeleteTextCommand(m_Lines, deletedStr, m_CaretLine, m_CaretColumn, false, SetCaretAction);
                    ExecuteHistory(new List<IEditCommand> { delCmd });
                }
                else
                {
                    string deletedChar = m_Lines[m_CaretLine].Substring(m_CaretColumn, 1);
                    var delCmd = new DeleteTextCommand(m_Lines, deletedChar, m_CaretLine, m_CaretColumn, false, SetCaretAction);
                    ExecuteHistory(new List<IEditCommand> { delCmd });
                }
            }
            else if (m_CaretLine < m_Lines.Count - 1)
            {
                var delCmd = new DeleteTextCommand(m_Lines, "\n", m_CaretLine, m_CaretColumn, true, SetCaretAction);
                ExecuteHistory(new List<IEditCommand> { delCmd });
            }
        }

        private int GetPreviousWordBoundary(int line, int col)
        {
            if (col <= 0) return 0;
            string text = m_Lines[line];
            col--;
            bool isSpace = char.IsWhiteSpace(text[col]);
            bool isWord = char.IsLetterOrDigit(text[col]) || text[col] == '_';
            while (col > 0)
            {
                char c = text[col - 1];
                if (isSpace && !char.IsWhiteSpace(c)) break;
                if (isWord && (!char.IsLetterOrDigit(c) && c != '_')) break;
                if (!isSpace && !isWord && (char.IsWhiteSpace(c) || char.IsLetterOrDigit(c) || c == '_')) break;
                col--;
            }
            return col;
        }

        private int GetNextWordBoundary(int line, int col)
        {
            string text = m_Lines[line];
            if (col >= text.Length) return text.Length;
            bool isSpace = char.IsWhiteSpace(text[col]);
            bool isWord = char.IsLetterOrDigit(text[col]) || text[col] == '_';
            while (col < text.Length)
            {
                char c = text[col];
                if (isSpace && !char.IsWhiteSpace(c)) break;
                if (isWord && (!char.IsLetterOrDigit(c) && c != '_')) break;
                if (!isSpace && !isWord && (char.IsWhiteSpace(c) || char.IsLetterOrDigit(c) || c == '_')) break;
                col++;
            }
            return col;
        }

        private void CopySelection()
        {
            if (!m_HasSelection)
            {
                if (m_Lines.Count == 0) return;
                EditorGUIUtility.systemCopyBuffer = m_Lines[m_CaretLine] + "\n";
                return;
            }
            GetSelectionRange(out int startL, out int startC, out int endL, out int endC);

            string copied = "";
            if (startL == endL)
            {
                copied = m_Lines[startL].Substring(startC, endC - startC);
            }
            else
            {
                copied = m_Lines[startL].Substring(startC) + "\n";
                for (int i = startL + 1; i < endL; i++) copied += m_Lines[i] + "\n";
                copied += m_Lines[endL].Substring(0, endC);
            }
            EditorGUIUtility.systemCopyBuffer = copied;
        }

        private void CutSelection()
        {
            if (!m_HasSelection)
            {
                if (m_Lines.Count == 0) return;
                EditorGUIUtility.systemCopyBuffer = m_Lines[m_CaretLine] + "\n";

                var cmds = new List<IEditCommand>();
                if (m_Lines.Count == 1)
                {
                    cmds.Add(new DeleteSelectionCommand(m_Lines, 0, 0, 0, m_Lines[0].Length, SetCaretAction));
                }
                else
                {
                    if (m_CaretLine < m_Lines.Count - 1)
                        cmds.Add(new DeleteSelectionCommand(m_Lines, m_CaretLine, 0, m_CaretLine + 1, 0, SetCaretAction));
                    else
                        cmds.Add(new DeleteSelectionCommand(m_Lines, m_CaretLine - 1, m_Lines[m_CaretLine - 1].Length, m_CaretLine, m_Lines[m_CaretLine].Length, SetCaretAction));
                }
                ExecuteHistory(cmds);
                return;
            }
            CopySelection();
            ExecuteHistory(new List<IEditCommand> { CreateDeleteSelectionCommand() });
        }

        private void DuplicateSelection()
        {
            if (m_Lines.Count == 0) return;

            if (!m_HasSelection)
            {
                // Duplicate current line
                string currentLine = m_Lines[m_CaretLine];
                ExecuteHistory(new List<IEditCommand> {
                    new InsertTextCommand(m_Lines, "\n" + currentLine, m_CaretLine, m_Lines[m_CaretLine].Length, SetCaretAction)
                });
                return;
            }

            // Duplicate selection
            GetSelectionRange(out int startL, out int startC, out int endL, out int endC);
            string textToDuplicate = "";
            if (startL == endL)
            {
                textToDuplicate = m_Lines[startL].Substring(startC, endC - startC);
            }
            else
            {
                textToDuplicate = m_Lines[startL].Substring(startC) + "\n";
                for (int i = startL + 1; i < endL; i++) textToDuplicate += m_Lines[i] + "\n";
                textToDuplicate += m_Lines[endL].Substring(0, endC);
            }

            ExecuteHistory(new List<IEditCommand> {
                new InsertTextCommand(m_Lines, textToDuplicate, endL, endC, SetCaretAction)
            });
        }

        private void MoveSelectedLinesUp()
        {
            if (m_Lines.Count <= 1) return;
            int startL = m_CaretLine, endL = m_CaretLine;
            if (m_HasSelection)
            {
                startL = Mathf.Min(m_AnchorLine, m_CaretLine);
                endL = Mathf.Max(m_AnchorLine, m_CaretLine);
            }
            if (startL == 0) return;

            ExecuteHistory(new List<IEditCommand> {
                new MoveLinesCommand(m_Lines, startL, endL, -1, () => {
                    m_CaretLine--;
                    if (m_HasSelection) m_AnchorLine--;
                })
            });
        }

        private void MoveSelectedLinesDown()
        {
            if (m_Lines.Count <= 1) return;
            int startL = m_CaretLine, endL = m_CaretLine;
            if (m_HasSelection)
            {
                startL = Mathf.Min(m_AnchorLine, m_CaretLine);
                endL = Mathf.Max(m_AnchorLine, m_CaretLine);
            }
            if (endL >= m_Lines.Count - 1) return;

            ExecuteHistory(new List<IEditCommand> {
                new MoveLinesCommand(m_Lines, startL, endL, 1, () => {
                    m_CaretLine++;
                    if (m_HasSelection) m_AnchorLine++;
                })
            });
        }

        private void PasteClipboard()
        {
            string pasteText = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(pasteText)) return;

            pasteText = pasteText.Replace("\r\n", "\n");
            int tabWidth = CodeIDESettings.Current.Editor.TabWidth;
            pasteText = pasteText.Replace("\t", new string(' ', tabWidth));

            var cmds = new List<IEditCommand>();
            if (m_HasSelection)
            {
                cmds.Add(CreateDeleteSelectionCommand());
                int newL = Mathf.Min(m_AnchorLine, m_CaretLine);
                int newC = m_AnchorLine == m_CaretLine ? Mathf.Min(m_AnchorColumn, m_CaretColumn) : (m_CaretLine < m_AnchorLine ? m_CaretColumn : m_AnchorColumn);
                cmds.Add(new InsertTextCommand(m_Lines, pasteText, newL, newC, SetCaretAction));
            }
            else
            {
                cmds.Add(new InsertTextCommand(m_Lines, pasteText, m_CaretLine, m_CaretColumn, SetCaretAction));
            }
            ExecuteHistory(cmds);
        }

        // -----------------------
        // Core Modifiers (Internal)
        // -----------------------

        // -----------------------
        // History & Saving
        // -----------------------
        private void DeleteSelectionIfNeeded()
        {
            if (!m_HasSelection) return;
            GetSelectionRange(out int startL, out int startC, out int endL, out int endC);

            if (startL == endL)
            {
                m_Lines[startL] = m_Lines[startL].Remove(startC, endC - startC);
            }
            else
            {
                string startPart = m_Lines[startL].Substring(0, startC);
                string endPart = m_Lines[endL].Substring(endC);
                m_Lines[startL] = startPart + endPart;
                if (endL - startL > 0)
                {
                    m_Lines.RemoveRange(startL + 1, endL - startL);
                }
            }

            m_HasSelection = false;
            m_CaretLine = startL;
            m_CaretColumn = startC;
        }
    }
}
