using System;
using System.Collections.Generic;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>Line-level editing commands: block indent/outdent, smart backspace, comment toggling and zoom.</summary>
    public partial class VirtualizedCodeViewer
    {
        private const int MinFontSize = 8;
        private const int MaxFontSize = 32;

        // Lines touched by a line command: the selection's lines (a selection ending at column 0 does not include
        // that last line), or just the caret line.
        private void GetCommandLineRange(out int startLine, out int endLine)
        {
            if (!m_HasSelection)
            {
                startLine = endLine = m_CaretLine;
                return;
            }

            GetSelectionRange(out startLine, out _, out endLine, out int endCol);
            if (endLine > startLine && endCol == 0) endLine--;
        }

        /// <summary>
        /// Tab: with a selection indents every selected line, otherwise inserts spaces up to the next tab stop.
        /// Shift+Tab outdents the selected lines (or the caret line).
        /// </summary>
        private void HandleTab(bool outdent)
        {
            int tabWidth = Mathf.Max(1, CodeIDESettings.Current.Editor.TabWidth);
            if (!outdent && !m_HasSelection)
            {
                InsertText(new string(' ', tabWidth - (m_CaretColumn % tabWidth)));
                return;
            }

            GetCommandLineRange(out int startLine, out int endLine);
            var cmds = new List<IEditCommand>();
            var deltas = new Dictionary<int, int>();

            for (int l = startLine; l <= endLine; l++)
            {
                string line = m_Lines[l];
                if (!outdent)
                {
                    // Blank lines inside a block stay blank instead of collecting trailing whitespace.
                    if (line.Length == 0 && startLine != endLine) continue;
                    cmds.Add(new InsertTextCommand(m_Lines, new string(' ', tabWidth), l, 0, SetCaretSilently));
                    deltas[l] = tabWidth;
                }
                else
                {
                    int n = 0;
                    while (n < tabWidth && n < line.Length && line[n] == ' ') n++;
                    if (n == 0) continue;
                    cmds.Add(new DeleteTextCommand(m_Lines, line.Substring(0, n), l, 0, false, SetCaretSilently));
                    deltas[l] = -n;
                }
            }

            ApplyLineCommands(cmds, deltas, keepColumnZero: !outdent);
        }

        /// <summary>Backspace inside leading indentation removes spaces back to the previous tab stop.</summary>
        private bool TrySmartBackspace()
        {
            if (m_HasSelection || m_CaretColumn == 0) return false;

            string line = m_Lines[m_CaretLine];
            if (m_CaretColumn > line.Length) return false;
            for (int i = 0; i < m_CaretColumn; i++)
            {
                if (line[i] != ' ') return false;
            }

            int tabWidth = Mathf.Max(1, CodeIDESettings.Current.Editor.TabWidth);
            int remove = m_CaretColumn % tabWidth == 0 ? tabWidth : m_CaretColumn % tabWidth;
            if (remove <= 1) return false; // a single space is an ordinary backspace (and keeps coalescing)

            int start = m_CaretColumn - remove;
            ExecuteHistory(new List<IEditCommand>
            {
                new DeleteSelectionCommand(m_Lines, m_CaretLine, start, m_CaretLine, m_CaretColumn, SetCaretAction)
            });
            return true;
        }

        private string GetLineCommentPrefix()
        {
            string path = CurrentFilePath ?? "";
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".json":
                case ".asmdef":
                case ".xml":
                case ".uxml":
                case ".md":
                case ".txt":
                    return null;
                default:
                    return "//";
            }
        }

        /// <summary>Ctrl+/: comments the lines out at their common indentation, or uncomments them if all are commented.</summary>
        private void ToggleLineComment()
        {
            string prefix = GetLineCommentPrefix();
            if (prefix == null) return;

            GetCommandLineRange(out int startLine, out int endLine);

            bool allCommented = true;
            bool anyNonBlank = false;
            int minIndent = int.MaxValue;
            for (int l = startLine; l <= endLine; l++)
            {
                string line = m_Lines[l];
                int indent = LeadingSpaces(line);
                if (indent == line.Length) continue;
                anyNonBlank = true;
                minIndent = Math.Min(minIndent, indent);
                if (string.CompareOrdinal(line, indent, prefix, 0, prefix.Length) != 0) allCommented = false;
            }
            if (!anyNonBlank) return;

            var cmds = new List<IEditCommand>();
            var positions = new Dictionary<int, int>();
            var deltas = new Dictionary<int, int>();
            for (int l = startLine; l <= endLine; l++)
            {
                string line = m_Lines[l];
                int indent = LeadingSpaces(line);
                if (indent == line.Length) continue;

                if (allCommented)
                {
                    int length = prefix.Length;
                    if (indent + length < line.Length && line[indent + length] == ' ') length++;
                    cmds.Add(new DeleteTextCommand(m_Lines, line.Substring(indent, length), l, indent, false, SetCaretSilently));
                    positions[l] = indent;
                    deltas[l] = -length;
                }
                else
                {
                    cmds.Add(new InsertTextCommand(m_Lines, prefix + " ", l, minIndent, SetCaretSilently));
                    positions[l] = minIndent;
                    deltas[l] = prefix.Length + 1;
                }
            }

            ApplyLineCommands(cmds, deltas, keepColumnZero: true, positions);
        }

        // Per-line commands only move the caret field; ApplyLineCommands (or the undo refresh) updates the visuals once
        // instead of once per touched line.
        private void SetCaretSilently(int line, int col)
        {
            m_CaretLine = line;
            m_CaretColumn = col;
            m_HasSelection = false;
        }

        private static int LeadingSpaces(string line)
        {
            int n = 0;
            while (n < line.Length && line[n] == ' ') n++;
            return n;
        }

        // Runs per-line commands as one undo step and keeps the caret/selection on the same text: a column moves by
        // the line's delta when it sits at or after the edit position (column 0 for indentation).
        private void ApplyLineCommands(List<IEditCommand> cmds, Dictionary<int, int> deltas, bool keepColumnZero,
            Dictionary<int, int> positions = null)
        {
            if (cmds.Count == 0) return;

            bool hadSelection = m_HasSelection;
            int anchorLine = m_AnchorLine, anchorCol = m_AnchorColumn;
            int caretLine = m_CaretLine, caretCol = m_CaretColumn;

            int Shift(int line, int col)
            {
                if (!deltas.TryGetValue(line, out int delta)) return col;
                int at = positions != null && positions.TryGetValue(line, out int p) ? p : 0;
                if (col < at) return col;
                if (keepColumnZero && col == 0 && at == 0 && hadSelection) return 0;
                return Mathf.Clamp(col + delta, at, m_Lines[line].Length);
            }

            ExecuteHistory(cmds);

            int newCaretCol = Shift(caretLine, caretCol);
            if (hadSelection)
            {
                m_AnchorLine = anchorLine;
                m_AnchorColumn = Shift(anchorLine, anchorCol);
            }
            SetCaretPosition(caretLine, newCaretCol, clearSelection: !hadSelection);
            m_HasSelection = hadSelection;
            RefreshListView();
        }

        /// <summary>Ctrl+Enter: opens an indented line below the caret line without splitting it.</summary>
        private void InsertLineBelow()
        {
            m_HasSelection = false;
            int line = m_CaretLine;
            string indent = new string(' ', LeadingSpaces(m_Lines[line]));
            ExecuteHistory(new List<IEditCommand> { new InsertTextCommand(m_Lines, "\n" + indent, line, m_Lines[line].Length, SetCaretAction) });
        }

        /// <summary>Ctrl+Shift+Enter: opens an indented line above the caret line.</summary>
        private void InsertLineAbove()
        {
            m_HasSelection = false;
            int line = m_CaretLine;
            string indent = new string(' ', LeadingSpaces(m_Lines[line]));
            ExecuteHistory(new List<IEditCommand> { new InsertTextCommand(m_Lines, indent + "\n", line, 0, SetCaretAction) });
            SetCaretPosition(line, indent.Length);
        }

        /// <summary>Ctrl+L: selects the caret line; repeating extends the selection by one line.</summary>
        private void SelectLine()
        {
            if (!m_HasSelection)
            {
                m_AnchorLine = m_CaretLine;
                m_AnchorColumn = 0;
            }

            int line = m_CaretColumn == 0 && m_HasSelection ? m_CaretLine : m_CaretLine + 1;
            if (line < m_Lines.Count) SetCaretPosition(line, 0, clearSelection: false);
            else SetCaretPosition(m_Lines.Count - 1, m_Lines[m_Lines.Count - 1].Length, clearSelection: false);
            m_HasSelection = true;
            UpdateCaretPosition();
        }

        private void Zoom(int delta)
        {
            var editor = CodeIDESettings.Current.Editor;
            int size = delta == 0 ? new EditorSettings().FontSize : Mathf.Clamp(editor.FontSize + delta, MinFontSize, MaxFontSize);
            if (size == editor.FontSize) return;
            editor.FontSize = size;
            CodeIDESettings.Save();
        }
    }
}
