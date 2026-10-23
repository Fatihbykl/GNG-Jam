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
        private static readonly Unity.Profiling.ProfilerMarker s_KeyDownMarker = new Unity.Profiling.ProfilerMarker("FuzzyIDE.CodeViewer.KeyDown");

        private void OnKeyDown(KeyDownEvent evt)
        {
            using (s_KeyDownMarker.Auto()) OnKeyDownCore(evt);
        }

        private void OnKeyDownCore(KeyDownEvent evt)
        {
            // Prevent intercepting input when a TextField (like Find/Replace) is focused, handling UI Toolkit's shadow DOM.
            VisualElement targetVE = evt.target as VisualElement;
            while (targetVE != null)
            {
                if (targetVE is TextField) return;
                if (targetVE == this) break;
                targetVE = targetVE.parent;
            }

            // Typing moves the text under any hover popup.
            DismissHoverPopups();

            bool handled = true;

            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.SemanticRename, evt))
            {
                OnRenameRequested?.Invoke(GetAbsoluteCaretPosition());
                evt.StopPropagation();
                return;
            }

            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.FindReferences, evt))
            {
                OnFindReferencesRequested?.Invoke(GetAbsoluteCaretPosition());
                evt.StopPropagation();
                return;
            }

            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.CodeCleanup, evt))
            {
                CleanupCodeAsync();
                evt.StopPropagation();
                return;
            }

            // Several overloads in signature help: Up/Down pick one (when no completion list is open).
            if ((evt.keyCode == KeyCode.UpArrow || evt.keyCode == KeyCode.DownArrow) && !evt.shiftKey && !evt.altKey && !evt.ctrlKey &&
                (m_AutoCompletePopup == null || !m_AutoCompletePopup.IsVisible) &&
                TryCycleSignatureOverload(evt.keyCode == KeyCode.UpArrow ? -1 : 1))
            {
                evt.StopPropagation();
                return;
            }

            // AutoComplete Navigation Interception
            if (m_AutoCompletePopup != null && m_AutoCompletePopup.IsVisible)
            {
                if (evt.keyCode == KeyCode.UpArrow)
                {
                    m_AutoCompletePopup.SelectPrevious();
                    evt.StopPropagation();
                    return;
                }
                else if (evt.keyCode == KeyCode.DownArrow)
                {
                    m_AutoCompletePopup.SelectNext();
                    evt.StopPropagation();
                    return;
                }
                else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.Tab)
                {
                    m_AutoCompletePopup.ConfirmSelection();
                    evt.StopPropagation();
#if UNITY_6000_0_OR_NEWER
                    this.panel?.focusController?.IgnoreEvent(evt);
#else
                    evt.PreventDefault();
#endif
                    return;
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    m_AutoCompletePopup.Hide();
                    evt.StopPropagation();
                    return;
                }
            }

            if (evt.keyCode == KeyCode.Escape)
            {
                if (m_Snippet != null)
                {
                    EndSnippetSession();
                    evt.StopPropagation();
                    return;
                }
                if (m_ExtraCarets.Count > 0)
                {
                    ClearExtraCarets();
                    evt.StopPropagation();
                    return;
                }
                if (m_SignatureHelpPopup.style.display == DisplayStyle.Flex)
                {
                    m_SignatureHelpPopup.style.display = DisplayStyle.None;
                    evt.StopPropagation();
                    return;
                }
            }

            // Trigger AutoComplete shortcut
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.AutoComplete, evt))
            {
                if (LanguageService != null && LanguageService.SupportsAutoComplete)
                {
                    ShowAutoCompleteAsync(true);
                }
                evt.StopPropagation();
                return;
            }

            // Go To Definition (F12 by default)
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.GoToDefinition, evt))
            {
                if (LanguageService != null && LanguageService.SupportsAutoComplete)
                {
                    GoToDefinitionAsync();
                }
                evt.StopPropagation();
                return;
            }

            // Quick Fix (Alt + Enter)
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.QuickFix, evt) || ((evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) && evt.altKey))
            {
                var scrollView = m_ListView.Q<ScrollView>();
                if (scrollView != null)
                {
                    float yPos = (m_CaretLine * LINE_HEIGHT) - scrollView.scrollOffset.y + LINE_HEIGHT;
                    int safeCol = Mathf.Clamp(m_CaretColumn, 0, m_Lines[m_CaretLine].Length);
                    float xPos = LINE_NUM_WIDTH + CODE_PADDING_LEFT + GetTextWidth(m_CaretLine, safeCol) - scrollView.scrollOffset.x;
                    Vector2 screenPos = m_ListView.LocalToWorld(new Vector2(xPos, yPos));

                    int absolutePosition = GetAbsoluteCaretPosition();
                    ShowContextMenuAsync(absolutePosition, screenPos);
                }
                evt.StopPropagation();
                return;
            }

            // File Saving
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Save, evt))
            {
                OnSaveRequested?.Invoke();
                evt.StopPropagation();
                return;
            }

            // Recompile & Reload Domain
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Recompile, evt))
            {
                OnRecompileRequested?.Invoke();
                evt.StopPropagation();
                return;
            }

            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.ReloadDomain, evt))
            {
                OnReloadDomainRequested?.Invoke();
                evt.StopPropagation();
                return;
            }

            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.OpenNotes, evt))
            {
                FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI.FuzzyNotesWindow.ShowWindow();
                evt.StopPropagation();
                return;
            }

            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.QuickScratchpad, evt))
            {
                FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI.QuickCaptureWindow.OpenQuickCapture();
                evt.StopPropagation();
                return;
            }

            // Find (Ctrl + F by default)
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Find, evt))
            {
                OpenFindPanel();
                evt.StopPropagation();
                return;
            }

            // Format Document (Ctrl + Alt + F or Ctrl + Alt + L)
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.FormatDocument, evt) || ((evt.keyCode == KeyCode.F || evt.keyCode == KeyCode.L) && (evt.ctrlKey || evt.commandKey) && evt.altKey))
            {
                if (LanguageService is CSharpLanguageService csharpService)
                {
                    FormatDocumentAsync(csharpService);
                }
                evt.StopPropagation();
                return;
            }

            // Replace (Ctrl + H by default)
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Replace, evt))
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
                        }
                    }
                }
                m_FindReplacePanel.FocusReplace();
                evt.StopPropagation();
                return;
            }

            // Undo (Ctrl + Z by default)
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Undo, evt))
            {
                if (m_History.Undo())
                {
                    OnTextChanged?.Invoke();
                    RefreshListView();
                }
                evt.StopPropagation();
                return;
            }

            // Redo (Ctrl + Y by default; Ctrl + Shift + Z always works too)
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Redo, evt) || (evt.keyCode == KeyCode.Z && (evt.ctrlKey || evt.commandKey) && evt.shiftKey))
            {
                if (m_History.Redo())
                {
                    OnTextChanged?.Invoke();
                    RefreshListView();
                }
                evt.StopPropagation();
                return;
            }

            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.SelectAll, evt))
            {
                SelectAll();
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Copy, evt))
            {
                CopySelection();
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Cut, evt))
            {
                CutSelection();
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.DuplicateLine, evt))
            {
                DuplicateSelection();
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.Paste, evt))
            {
                PasteClipboard();
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.ToggleLineComment, evt) ||
                (evt.keyCode == KeyCode.KeypadDivide && (evt.ctrlKey || evt.commandKey)))
            {
                ToggleLineComment();
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.ZoomIn, evt) || (evt.keyCode == KeyCode.KeypadPlus && (evt.ctrlKey || evt.commandKey)))
            {
                Zoom(1);
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.ZoomOut, evt) || (evt.keyCode == KeyCode.KeypadMinus && (evt.ctrlKey || evt.commandKey)))
            {
                Zoom(-1);
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.ZoomReset, evt))
            {
                Zoom(0);
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.InsertLineBelow, evt))
            {
                InsertLineBelow();
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.InsertLineAbove, evt))
            {
                InsertLineAbove();
                evt.StopPropagation();
                return;
            }
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.SelectLine, evt))
            {
                SelectLine();
                evt.StopPropagation();
                return;
            }

            if (evt.keyCode == KeyCode.Backspace)
            {
                if (m_ExtraCarets.Count > 0)
                {
                    MultiBackspace();
                }
                else
                {
                    if (m_AutoCompletePopup != null && m_AutoCompletePopup.IsVisible)
                    {
                        if (m_AutoCompletePrefix.Length > 0)
                        {
                            m_AutoCompletePrefix = m_AutoCompletePrefix.Substring(0, m_AutoCompletePrefix.Length - 1);
                            m_AutoCompletePopup.Filter(m_AutoCompletePrefix);
                        }
                        else
                        {
                            m_AutoCompletePopup.Hide();
                        }
                    }
                    bool isShortcut = Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer ? evt.commandKey : evt.ctrlKey;
                    if (isShortcut || !TrySmartBackspace()) Backspace(isShortcut);
                }
            }
            else if (evt.keyCode == KeyCode.Delete)
            {
                if (m_ExtraCarets.Count > 0)
                {
                    MultiDelete();
                }
                else
                {
                    bool isShortcut = Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer ? evt.commandKey : evt.ctrlKey;
                    Delete(isShortcut);
                }
            }
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                string currentLine = m_Lines[m_CaretLine];
                string leadingWhitespace = new string(currentLine.TakeWhile(char.IsWhiteSpace).ToArray());

                bool isBetweenBraces = !m_HasSelection && m_CaretColumn > 0 && m_CaretColumn < currentLine.Length && currentLine[m_CaretColumn - 1] == '{' && currentLine[m_CaretColumn] == '}';
                int openBraces = m_Lines.Sum(l => l.Count(c => c == '{'));
                int closeBraces = m_Lines.Sum(l => l.Count(c => c == '}'));
                bool needsClose = openBraces > closeBraces;

                bool autoClose = CodeIDESettings.Current.Behaviors.AutoCloseBrackets && needsClose && m_CaretColumn > 0 && currentLine.Substring(0, m_CaretColumn).TrimEnd().EndsWith("{");

                if (isBetweenBraces)
                {
                    string indentedSpace = leadingWhitespace + "    ";
                    string block = "\n" + indentedSpace + "\n" + leadingWhitespace;
                    int baseLine = m_CaretLine;

                    InsertText(block);
                    SetCaretPosition(baseLine + 1, indentedSpace.Length, clearSelection: true);
                    RefreshListView();
                }
                else if (autoClose)
                {
                    string indentedSpace = leadingWhitespace + "    ";
                    string block = "\n" + indentedSpace + "\n" + leadingWhitespace + "}";
                    int baseLine = m_HasSelection ? Mathf.Min(m_AnchorLine, m_CaretLine) : m_CaretLine;

                    InsertText(block);

                    SetCaretPosition(baseLine + 1, indentedSpace.Length, clearSelection: true);
                    RefreshListView();
                }
                else
                {
                    InsertText("\n" + leadingWhitespace);
                }
            }
            else if (evt.keyCode == KeyCode.Tab)
            {
                if (m_ExtraCarets.Count > 0)
                {
                    MultiInsertText(new string(' ', CodeIDESettings.Current.Editor.TabWidth));
                }
                else if (!TryAdvanceSnippet(evt.shiftKey))
                {
                    HandleTab(evt.shiftKey);
                }
                evt.StopPropagation();
#if UNITY_6000_0_OR_NEWER
                this.panel?.focusController?.IgnoreEvent(evt);
#else
                evt.PreventDefault();
#endif
            }
            else if (evt.keyCode == KeyCode.LeftArrow)
            {
                if (m_ExtraCarets.Count > 0)
                {
                    MultiMoveCaretLeft(evt.shiftKey);
                    evt.StopPropagation();
                }
                else
                {
                    bool isShortcut = Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer ? evt.commandKey : evt.ctrlKey;
                    if (isShortcut)
                    {
                        HandleSelectionBeforeMove(evt.shiftKey);
                        if (m_CaretColumn > 0)
                        {
                            int newCol = GetPreviousWordBoundary(m_CaretLine, m_CaretColumn);
                            SetCaretPosition(m_CaretLine, newCol, !evt.shiftKey);
                        }
                        else if (m_CaretLine > 0)
                        {
                            SetCaretPosition(m_CaretLine - 1, m_Lines[m_CaretLine - 1].Length, !evt.shiftKey);
                        }
                    }
                    else MoveCaretLeft(evt.shiftKey);
                }
            }
            else if (evt.keyCode == KeyCode.RightArrow)
            {
                if (m_ExtraCarets.Count > 0)
                {
                    MultiMoveCaretRight(evt.shiftKey);
                    evt.StopPropagation();
                }
                else
                {
                    bool isShortcut = Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer ? evt.commandKey : evt.ctrlKey;
                    if (isShortcut)
                    {
                        HandleSelectionBeforeMove(evt.shiftKey);
                        if (m_CaretColumn < m_Lines[m_CaretLine].Length)
                        {
                            int newCol = GetNextWordBoundary(m_CaretLine, m_CaretColumn);
                            SetCaretPosition(m_CaretLine, newCol, !evt.shiftKey);
                        }
                        else if (m_CaretLine < m_Lines.Count - 1)
                        {
                            SetCaretPosition(m_CaretLine + 1, 0, !evt.shiftKey);
                        }
                    }
                    else MoveCaretRight(evt.shiftKey);
                }
            }
            else if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.DeleteLine, evt))
            {
                if (m_Lines.Count > 1)
                {
                    int delEndLine = m_CaretLine < m_Lines.Count - 1 ? m_CaretLine + 1 : m_CaretLine;
                    int delEndCol = m_CaretLine < m_Lines.Count - 1 ? 0 : m_Lines[m_CaretLine].Length;
                    int delStartLine = m_CaretLine < m_Lines.Count - 1 ? m_CaretLine : m_CaretLine - 1;
                    int delStartCol = m_CaretLine < m_Lines.Count - 1 ? 0 : m_Lines[m_CaretLine - 1].Length;

                    ExecuteHistory(new List<IEditCommand> {
                        new DeleteSelectionCommand(m_Lines, delStartLine, delStartCol, delEndLine, delEndCol, SetCaretAction)
                    });
                }
                else
                {
                    ExecuteHistory(new List<IEditCommand> {
                        new DeleteSelectionCommand(m_Lines, 0, 0, 0, m_Lines[0].Length, SetCaretAction)
                    });
                }
            }
            else if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.MoveLineUp, evt))
            {
                MoveSelectedLinesUp();
            }
            else if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.MoveLineDown, evt))
            {
                MoveSelectedLinesDown();
            }
            else if (evt.keyCode == KeyCode.UpArrow)
            {
                if (evt.ctrlKey || evt.commandKey)
                {
                    AddExtraCaretUp();
                    evt.StopPropagation();
                }
                else
                {
                    ClearExtraCarets();
                    MoveCaretUp(evt.shiftKey);
                }
            }
            else if (evt.keyCode == KeyCode.DownArrow)
            {
                if (evt.ctrlKey || evt.commandKey)
                {
                    AddExtraCaretDown();
                    evt.StopPropagation();
                }
                else
                {
                    ClearExtraCarets();
                    MoveCaretDown(evt.shiftKey);
                }
            }
            else if (evt.keyCode == KeyCode.Home)
            {
                HandleSelectionBeforeMove(evt.shiftKey);
                bool isShortcut = Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer ? evt.commandKey : evt.ctrlKey;
                if (isShortcut)
                {
                    SetCaretPosition(0, 0, !evt.shiftKey);
                }
                else
                {
                    int col = 0;
                    string line = m_Lines[m_CaretLine];
                    while (col < line.Length && char.IsWhiteSpace(line[col])) col++;
                    if (m_CaretColumn == col) col = 0;
                    SetCaretPosition(m_CaretLine, col, !evt.shiftKey);
                }
            }
            else if (evt.keyCode == KeyCode.End)
            {
                HandleSelectionBeforeMove(evt.shiftKey);
                bool isShortcut = Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer ? evt.commandKey : evt.ctrlKey;
                if (isShortcut)
                {
                    SetCaretPosition(m_Lines.Count - 1, m_Lines[m_Lines.Count - 1].Length, !evt.shiftKey);
                }
                else
                {
                    SetCaretPosition(m_CaretLine, m_Lines[m_CaretLine].Length, !evt.shiftKey);
                }
            }
            else if (evt.character != '\0' && !char.IsControl(evt.character) && (!(Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer ? evt.commandKey : evt.ctrlKey) || (evt.ctrlKey && evt.altKey)))
            {
                if (m_ExtraCarets.Count > 0)
                {
                    MultiInsertText(evt.character.ToString());
                }
                else
                {
                    char ch = evt.character;
                string lineText = m_Lines[m_CaretLine];

                // 1. Overtype closing character
                if (!m_HasSelection && m_CaretColumn < lineText.Length && lineText[m_CaretColumn] == ch && (ch == ')' || ch == '}' || ch == ']' || ch == '"' || ch == '\''))
                {
                    SetCaretPosition(m_CaretLine, m_CaretColumn + 1, clearSelection: true);
                }
                // 2. Selection wrapping
                else if (m_HasSelection && (ch == '(' || ch == '{' || ch == '[' || ch == '"' || ch == '\''))
                {
                    char closeCh = ch == '(' ? ')' : (ch == '{' ? '}' : (ch == '[' ? ']' : ch));
                    GetSelectionRange(out int sL, out int sC, out int eL, out int eC);
                    string selectedText = GetSelectedText();
                    var cmds = new List<IEditCommand>();
                    cmds.Add(CreateDeleteSelectionCommand());
                    cmds.Add(new InsertTextCommand(m_Lines, ch + selectedText + closeCh, sL, sC, SetCaretAction));
                    ExecuteHistory(cmds);
                    SetCaretPosition(sL, sC + 1, clearSelection: false);
                    m_AnchorLine = eL;
                    m_AnchorColumn = (sL == eL) ? eC + 1 : eC;
                    m_HasSelection = true;
                }
                // 3. Auto-close pair insertion
                else if (!m_HasSelection && CodeIDESettings.Current.Behaviors.AutoCloseBrackets && (ch == '(' || ch == '{' || ch == '[' || ch == '"' || ch == '\''))
                {
                    bool isQuote = ch == '"' || ch == '\'';
                    bool prevIsWordChar = m_CaretColumn > 0 && (char.IsLetterOrDigit(lineText[m_CaretColumn - 1]) || lineText[m_CaretColumn - 1] == '_');
                    bool nextIsWordChar = m_CaretColumn < lineText.Length && char.IsLetterOrDigit(lineText[m_CaretColumn]);

                    if (isQuote && prevIsWordChar)
                    {
                        InsertText(ch.ToString());
                    }
                    else if (!nextIsWordChar)
                    {
                        char closeCh = ch == '(' ? ')' : (ch == '{' ? '}' : (ch == '[' ? ']' : ch));
                        InsertText(ch.ToString() + closeCh.ToString());
                        SetCaretPosition(m_CaretLine, m_CaretColumn - 1, clearSelection: true);
                    }
                    else
                    {
                        InsertText(ch.ToString());
                    }
                }
                else
                {
                    InsertText(ch.ToString());
                }

                if (evt.character == '(' || evt.character == ',')
                {
                    m_DebounceTask.ExecuteLater(50); // Fast trigger for signature help
                }
                else if (evt.character == ')')
                {
                    m_SignatureHelpPopup.style.display = DisplayStyle.None;
                    m_DebounceTask.ExecuteLater(50);
                }

                string curLine = m_Lines[m_CaretLine];
                bool inComment = curLine.Contains("//") && curLine.IndexOf("//") < m_CaretColumn;
                bool inString = curLine.Substring(0, m_CaretColumn).Count(c => c == '"') % 2 != 0;

                if (inString && !inComment && LanguageService != null && LanguageService.SupportsAutoComplete &&
                    CodeIDESettings.Current.Unity.UnityStringIntelligence)
                {
                    // Re-queried per keystroke: the popup only opens when the literal names a tag/layer/scene/...
                    ShowAutoCompleteAsync(true);
                }
                else if (inComment || inString)
                {
                    m_AutoCompletePopup?.Hide();
                }
                else
                {
                    bool wasVisible = false;
                    if (m_AutoCompletePopup != null && m_AutoCompletePopup.IsVisible)
                    {
                        wasVisible = true;
                        if (char.IsLetterOrDigit(evt.character) || evt.character == '_')
                        {
                            m_AutoCompletePrefix += evt.character;
                            m_AutoCompletePopup.Filter(m_AutoCompletePrefix);
                            if (!m_AutoCompletePopup.HasMatches()) m_AutoCompletePopup.Hide();
                        }
                        else
                        {
                            m_AutoCompletePopup.Hide();
                        }
                    }

                    if (LanguageService != null && LanguageService.SupportsAutoComplete)
                    {
                        if (evt.character == '.')
                        {
                            ShowAutoCompleteAsync(false);
                        }
                        else if (evt.character == ' ' && m_CaretColumn >= 9 && curLine.Substring(0, m_CaretColumn).EndsWith("override "))
                        {
                            ShowAutoCompleteAsync(true);
                        }
                        else if (!wasVisible && (char.IsLetter(evt.character) || evt.character == '_'))
                        {
                            ShowAutoCompleteAsync(true);
                        }
                    }
                }
            }
            }
            else
            {
                handled = false;
            }

            if (handled) evt.StopPropagation();
        }

        // -----------------------
        // Caret Movement
        // -----------------------
        private void HandleSelectionBeforeMove(bool shift)
        {
            if (shift && !m_HasSelection)
            {
                m_HasSelection = true;
                m_AnchorLine = m_CaretLine;
                m_AnchorColumn = m_CaretColumn;
            }
        }

        private void MoveCaretLeft(bool shift)
        {
            HandleSelectionBeforeMove(shift);
            if (m_CaretColumn > 0)
                SetCaretPosition(m_CaretLine, m_CaretColumn - 1, !shift);
            else if (m_CaretLine > 0)
                SetCaretPosition(m_CaretLine - 1, m_Lines[m_CaretLine - 1].Length, !shift);
        }

        private void MoveCaretRight(bool shift)
        {
            HandleSelectionBeforeMove(shift);
            if (m_CaretColumn < m_Lines[m_CaretLine].Length)
                SetCaretPosition(m_CaretLine, m_CaretColumn + 1, !shift);
            else if (m_CaretLine < m_Lines.Count - 1)
                SetCaretPosition(m_CaretLine + 1, 0, !shift);
        }

        private void MoveCaretUp(bool shift)
        {
            HandleSelectionBeforeMove(shift);
            int visIdx = GetVisibleLineIndex(m_CaretLine);
            if (visIdx > 0)
            {
                int newVisIdx = visIdx - 1;
                while (newVisIdx >= 0 && m_VisibleLines[newVisIdx] < 0) newVisIdx--;
                if (newVisIdx >= 0)
                {
                    int newLine = m_VisibleLines[newVisIdx];
                    int newCol = Mathf.Min(m_CaretColumn, m_Lines[newLine].Length);
                    SetCaretPosition(newLine, newCol, !shift);
                }
            }
        }

        private void MoveCaretDown(bool shift)
        {
            HandleSelectionBeforeMove(shift);
            int visIdx = GetVisibleLineIndex(m_CaretLine);
            if (visIdx >= 0 && visIdx < m_VisibleLines.Count - 1)
            {
                int newVisIdx = visIdx + 1;
                while (newVisIdx < m_VisibleLines.Count && m_VisibleLines[newVisIdx] < 0) newVisIdx++;
                if (newVisIdx < m_VisibleLines.Count)
                {
                    int newLine = m_VisibleLines[newVisIdx];
                    int newCol = Mathf.Min(m_CaretColumn, m_Lines[newLine].Length);
                    SetCaretPosition(newLine, newCol, !shift);
                }
            }
        }

        private void SelectAll()
        {
            if (m_Lines.Count == 0) return;
            m_AnchorLine = 0;
            m_AnchorColumn = 0;
            m_HasSelection = true;
            SetCaretPosition(m_Lines.Count - 1, m_Lines[m_Lines.Count - 1].Length, clearSelection: false);
        }

        // -----------------------
        // Mouse Events
        // -----------------------
        private void OnPointerDown(PointerDownEvent evt)
        {
            ClearExtraCarets();
            if (m_SignatureHelpPopup != null) m_SignatureHelpPopup.style.display = DisplayStyle.None;
            if (m_HoverInfoPopup != null && m_HoverInfoPopup.style.display == DisplayStyle.Flex)
            {
                m_IsHoveringDiagnostic = false;
                m_HoverInfoPopup.style.display = DisplayStyle.None;
            }

            this.Focus();
            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;

            Vector2 localPos = m_ListView.WorldToLocal(evt.position);
            GetLineAndCol(localPos, scrollView, out int line, out int col);

            if (evt.button == 1) // Right click
            {
                bool isInsideSelection = false;
                if (m_HasSelection)
                {
                    GetSelectionRange(out int startL, out int startC, out int endL, out int endC);
                    if (line > startL && line < endL) isInsideSelection = true;
                    else if (line == startL && line == endL && col >= startC && col <= endC) isInsideSelection = true;
                    else if (line == startL && line < endL && col >= startC) isInsideSelection = true;
                    else if (line == endL && line > startL && col <= endC) isInsideSelection = true;
                }

                if (!isInsideSelection)
                {
                    SetCaretPosition(line, col, clearSelection: true);
                }

                int absolutePosition = 0;
                for (int i = 0; i < line; i++) absolutePosition += m_Lines[i].Length + 1;
                absolutePosition += col;

                ShowContextMenuAsync(absolutePosition, evt.position);
                evt.StopPropagation();
                return;
            }

            if (evt.button == 0 && (evt.ctrlKey || evt.commandKey) && !evt.shiftKey && !evt.altKey)
            {
                SetCaretPosition(line, col, clearSelection: true);
                int absolutePosition = GetAbsoluteIndex(line, col);
                HandleCtrlClickAsync(absolutePosition);
                evt.StopPropagation();
                return;
            }

            m_IsDragging = true;
            m_ListView.CapturePointer(evt.pointerId);

            double currentTime = UnityEditor.EditorApplication.timeSinceStartup;
            if (currentTime - m_LastClickTime < 0.5 && m_LastClickLine == line)
            {
                m_CustomClickCount++;
            }
            else
            {
                m_CustomClickCount = 1;
            }
            m_LastClickTime = currentTime;
            m_LastClickLine = line;

            if (m_CustomClickCount >= 3 || evt.clickCount >= 3)
            {
                m_AnchorLine = line;
                m_AnchorColumn = 0;
                m_HasSelection = true;
                SetCaretPosition(line, m_Lines[line].Length, clearSelection: false);
                return;
            }
            else if (m_CustomClickCount == 2 || evt.clickCount == 2)
            {
                string text = m_Lines[line];
                if (text.Length > 0)
                {
                    int clickedCharIndex = Mathf.Clamp(col, 0, text.Length - 1);
                    char c = text[clickedCharIndex];

                    bool isSpace = char.IsWhiteSpace(c);
                    bool isWord = char.IsLetterOrDigit(c) || c == '_';

                    int sCol = clickedCharIndex;
                    while (sCol > 0)
                    {
                        char prev = text[sCol - 1];
                        bool prevIsSpace = char.IsWhiteSpace(prev);
                        bool prevIsWord = char.IsLetterOrDigit(prev) || prev == '_';

                        if (isSpace && !prevIsSpace) break;
                        if (isWord && !prevIsWord) break;
                        if (!isSpace && !isWord && (prevIsSpace || prevIsWord)) break;

                        sCol--;
                    }

                    int eCol = clickedCharIndex;
                    while (eCol < text.Length)
                    {
                        char next = text[eCol];
                        bool nextIsSpace = char.IsWhiteSpace(next);
                        bool nextIsWord = char.IsLetterOrDigit(next) || next == '_';

                        if (isSpace && !nextIsSpace) break;
                        if (isWord && !nextIsWord) break;
                        if (!isSpace && !isWord && (nextIsSpace || nextIsWord)) break;

                        eCol++;
                    }

                    m_AnchorLine = line;
                    m_AnchorColumn = sCol;
                    m_HasSelection = true;
                    SetCaretPosition(line, eCol, clearSelection: false);
                    return;
                }
            }

            if (evt.shiftKey)
            {
                if (!m_HasSelection)
                {
                    m_AnchorLine = m_CaretLine;
                    m_AnchorColumn = m_CaretColumn;
                    m_HasSelection = true;
                }
            }
            else
            {
                m_AnchorLine = line;
                m_AnchorColumn = col;
                m_HasSelection = false;
            }

            SetCaretPosition(line, col, clearSelection: !evt.shiftKey && !m_HasSelection);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            HandleHoverInfo(evt);
            if (m_IsDragging)
            {
                var scrollView = m_ListView.Q<ScrollView>();
                if (scrollView != null)
                {
                    Vector2 localPos = m_ListView.WorldToLocal(evt.position);
                    GetLineAndCol(localPos, scrollView, out int line, out int col);
                    m_HasSelection = true;
                    SetCaretPosition(line, col, clearSelection: false);
                }
                evt.StopPropagation();
            }
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (m_IsDragging)
            {
                m_IsDragging = false;
                m_ListView.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            }
        }

        private void GetLineAndCol(Vector2 localPos, ScrollView scrollView, out int lineIndex, out int colIndex)
        {
            if (m_Lines == null || m_Lines.Count == 0)
            {
                lineIndex = 0;
                colIndex = 0;
                return;
            }

            float virtualY = localPos.y + scrollView.scrollOffset.y;
            int visIdx = Mathf.FloorToInt(virtualY / LINE_HEIGHT);

            if (visIdx < 0) visIdx = 0;
            if (visIdx >= m_VisibleLines.Count) visIdx = m_VisibleLines.Count - 1;
        
            lineIndex = m_VisibleLines.Count > 0 ? m_VisibleLines[visIdx] : 0;
            if (lineIndex >= m_Lines.Count)
            {
                // The text shrank without this view being refreshed (e.g. edited through another pane).
                RecalculateVisibleLines();
                lineIndex = m_Lines.Count - 1;
            }

            if (lineIndex < 0)
            {
                lineIndex = m_PendingDiff != null ? m_PendingDiff.StartLine : 0;
                if (lineIndex >= m_Lines.Count) lineIndex = m_Lines.Count - 1;
                if (lineIndex < 0) lineIndex = 0;
            }

            float virtualX = localPos.x + scrollView.scrollOffset.x;
            float textStartX = LINE_NUM_WIDTH + CODE_PADDING_LEFT;
            float relativeX = virtualX - textStartX;

            colIndex = 0;
            if (relativeX > 0)
            {
                string lineText = m_Lines[lineIndex];
                int bestCol = 0;
                float minDiff = float.MaxValue;
                for (int i = 0; i <= lineText.Length; i++)
                {
                    float w = GetTextWidth(lineIndex, i);
                    float diff = Mathf.Abs(w - relativeX);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        bestCol = i;
                    }
                    else if (w > relativeX)
                    {
                        break;
                    }
                }
                colIndex = bestCol;
            }

            int lineLength = m_Lines[lineIndex].Length;
            if (colIndex > lineLength) colIndex = lineLength;
        }

        private async void HandleCtrlClickAsync(int absoluteIndex)
        {
            if (!(LanguageService is CSharpLanguageService csharpService)) return;

            var location = await csharpService.Roslyn.GetDefinitionLocationAsync(absoluteIndex);

            if (location == null || location == Microsoft.CodeAnalysis.Location.None)
            {
                OnFindReferencesRequested?.Invoke(absoluteIndex);
                return;
            }

            var lineSpan = location.GetLineSpan();
            int lineIndex = lineSpan.StartLinePosition.Line;
            string filePath = location.SourceTree?.FilePath;

            bool isSameFile = string.IsNullOrEmpty(filePath) || filePath == CurrentFilePath;
            bool isSameLine = lineIndex == m_CaretLine ||
                (lineSpan.StartLinePosition.Line <= m_CaretLine && lineSpan.EndLinePosition.Line >= m_CaretLine);

            if (isSameFile && isSameLine)
            {
                // Clicked on declaration (function, class, property, etc.): like in Rider, search references!
                OnFindReferencesRequested?.Invoke(absoluteIndex);
            }
            else
            {
                // Clicked on a usage: navigate to definition (like in Rider)
                if (!string.IsNullOrEmpty(filePath) && filePath != CurrentFilePath)
                {
                    OnLoadFileRequested?.Invoke(filePath, lineIndex + 1); // 1-indexed for Unity
                }
                else
                {
                    JumpToLine(lineIndex);
                }
            }
        }

        private async void GoToDefinitionAsync()
        {
            if (!(LanguageService is CSharpLanguageService csharpService)) return;

            int absoluteIndex = GetAbsoluteIndex(m_CaretLine, m_CaretColumn);
            var location = await csharpService.Roslyn.GetDefinitionLocationAsync(absoluteIndex);

            if (location == null || location == Microsoft.CodeAnalysis.Location.None) return;

            var lineSpan = location.GetLineSpan();
            int lineIndex = lineSpan.StartLinePosition.Line;

            string filePath = location.SourceTree?.FilePath;

            if (!string.IsNullOrEmpty(filePath) && filePath != CurrentFilePath)
            {
                OnLoadFileRequested?.Invoke(filePath, lineIndex + 1); // 1-indexed for Unity
            }
            else
            {
                if (lineIndex == m_CaretLine)
                {
                    OnFindReferencesRequested?.Invoke(absoluteIndex);
                }
                else
                {
                    JumpToLine(lineIndex);
                }
            }
        }
    }
}
