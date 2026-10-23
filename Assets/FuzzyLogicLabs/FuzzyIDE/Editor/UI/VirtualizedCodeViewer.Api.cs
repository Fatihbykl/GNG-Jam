using System;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>Entry points for the window: command palette actions, search result selection and navigation history.</summary>
    public partial class VirtualizedCodeViewer
    {
        /// <summary>Raised before a programmatic jump with (current line, target line), so the window can record history.</summary>
        public event Action<int, int> OnJumpRequested;

        /// <summary>Moves the caret to <paramref name="line"/> and selects [startCol, endCol) on it.</summary>
        /// <summary>
        /// The line list was changed by someone else (the same file open in the other pane shares it): drop caches
        /// derived from the old text and rebuild the visible rows.
        /// </summary>
        public void RefreshAfterSharedEdit()
        {
            if (m_Lines == null) return;
            if (m_Lines.Count == 0) m_Lines.Add("");
            m_LineMarkupCache.Clear();
            m_HasSelection = false;
            m_CaretLine = Mathf.Clamp(m_CaretLine, 0, m_Lines.Count - 1);
            m_CaretColumn = Mathf.Clamp(m_CaretColumn, 0, m_Lines[m_CaretLine].Length);
            RefreshListView();
        }

        public void SelectRange(int line, int startCol, int endCol)
        {
            if (m_Lines == null || m_Lines.Count == 0) return;
            line = Mathf.Clamp(line, 0, m_Lines.Count - 1);
            int length = m_Lines[line].Length;
            startCol = Mathf.Clamp(startCol, 0, length);
            endCol = Mathf.Clamp(endCol, startCol, length);

            JumpToLine(line);
            m_AnchorLine = line;
            m_AnchorColumn = startCol;
            SetCaretPosition(line, endCol, clearSelection: endCol == startCol);
            m_HasSelection = endCol > startCol;
            UpdateCaretPosition();
        }

        /// <summary>The selected text when it lies on one line (used to prefill search boxes), otherwise null.</summary>
        public string GetSingleLineSelection()
        {
            if (!m_HasSelection) return null;
            GetSelectionRange(out int sL, out int sC, out int eL, out int eC);
            if (sL != eL || eC <= sC) return null;
            return m_Lines[sL].Substring(sC, Mathf.Min(eC, m_Lines[sL].Length) - sC);
        }

        public string GetText() => string.Join("\n", m_Lines);

        /// <summary>Selected characters (line breaks count as one); 0 without a selection.</summary>
        public int SelectedCharCount
        {
            get
            {
                if (!m_HasSelection) return 0;
                GetSelectionRange(out int sL, out int sC, out int eL, out int eC);
                if (sL == eL) return Mathf.Max(0, eC - sC);
                int count = m_Lines[sL].Length - sC + 1;
                for (int l = sL + 1; l < eL; l++) count += m_Lines[l].Length + 1;
                return count + eC;
            }
        }

        public int SelectedLineCount
        {
            get
            {
                if (!m_HasSelection) return 0;
                GetSelectionRange(out int sL, out _, out int eL, out _);
                return eL - sL + 1;
            }
        }

        /// <summary>World rect of columns [startCol, endCol) of a line, for anchoring popups to text.</summary>
        public Rect GetTextRangeWorldRect(int line, int startCol, int endCol)
        {
            line = Mathf.Clamp(line, 0, m_Lines.Count - 1);
            int length = m_Lines[line].Length;
            var offset = m_ListView.Q<ScrollView>()?.scrollOffset ?? Vector2.zero;
            float textLeft = LINE_NUM_WIDTH + CODE_PADDING_LEFT - offset.x;
            float top = Mathf.Max(0, GetVisibleLineIndex(line)) * LINE_HEIGHT - offset.y;
            return PopupPlacement.LineAnchor(this, textLeft + GetTextWidth(line, Mathf.Clamp(startCol, 0, length)),
                textLeft + GetTextWidth(line, Mathf.Clamp(endCol, 0, length)), top, LINE_HEIGHT);
        }

        /// <summary>Runs an editor action as if its shortcut was pressed (command palette). False when it isn't an editor action.</summary>
        public bool ExecuteAction(IDEAction action)
        {
            switch (action)
            {
                case IDEAction.FormatDocument:
                    if (LanguageService is CSharpLanguageService csharpService) FormatDocumentAsync(csharpService);
                    return true;
                case IDEAction.ToggleLineComment: ToggleLineComment(); return true;
                case IDEAction.CodeCleanup: CleanupCodeAsync(); return true;
                case IDEAction.ZoomIn: Zoom(1); return true;
                case IDEAction.ZoomOut: Zoom(-1); return true;
                case IDEAction.ZoomReset: Zoom(0); return true;
                case IDEAction.SelectAll: SelectAll(); return true;
                case IDEAction.GoToDefinition: GoToDefinitionAsync(); return true;
                case IDEAction.Find: OpenFindPanel(); return true;
                case IDEAction.DuplicateLine: DuplicateSelection(); return true;
                case IDEAction.MoveLineUp: MoveSelectedLinesUp(); return true;
                case IDEAction.MoveLineDown: MoveSelectedLinesDown(); return true;
                case IDEAction.InsertLineBelow: InsertLineBelow(); return true;
                case IDEAction.InsertLineAbove: InsertLineAbove(); return true;
                case IDEAction.SemanticRename: OnRenameRequested?.Invoke(GetAbsoluteCaretPosition()); return true;
                case IDEAction.FindReferences: OnFindReferencesRequested?.Invoke(GetAbsoluteCaretPosition()); return true;
                case IDEAction.Undo:
                    if (m_History.Undo()) { OnTextChanged?.Invoke(); RefreshListView(); }
                    return true;
                case IDEAction.Redo:
                    if (m_History.Redo()) { OnTextChanged?.Invoke(); RefreshListView(); }
                    return true;
                default:
                    return false;
            }
        }
    }
}
