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
        // F12 Navigation
        // -----------------------
        private int GetAbsoluteIndex(int line, int col)
        {
            if (m_Lines == null || m_Lines.Count == 0) return 0;
            int clampedLine = Mathf.Clamp(line, 0, m_Lines.Count - 1);
            int index = 0;
            for (int i = 0; i < clampedLine; i++)
            {
                index += m_Lines[i].Length + 1; // +1 for newline
            }
            return index + Mathf.Clamp(col, 0, m_Lines[clampedLine].Length);
        }

        // -----------------------
        // UI Visual Elements
        // -----------------------
        public void SetCaretPosition(int line, int col, bool clearSelection = true)
        {
            if (clearSelection) m_HasSelection = false;
            int oldLine = m_CaretLine;

            m_CaretLine = line;
            m_CaretColumn = col;

            m_IsCaretVisible = true;
            m_Caret.style.display = DisplayStyle.Flex;

            EnsureCaretVisible();
            UpdateCaretPosition();
            OnCaretPositionChanged?.Invoke(m_CaretLine + 1, m_CaretColumn + 1);
            
            if (oldLine != m_CaretLine)
            {
                if (m_SignatureHelpPopup != null) m_SignatureHelpPopup.style.display = DisplayStyle.None;
                m_ListView.RefreshItems();
            }

            if (m_BlinkTask != null)
            {
                m_BlinkTask.Pause();
                m_BlinkTask.StartingIn(500);
                m_BlinkTask.Resume();
            }
        }

        public int GetAbsoluteCaretPosition()
        {
            int index = 0;
            for (int i = 0; i < m_CaretLine; i++)
            {
                index += m_Lines[i].Length + 1; // +1 for \n
            }
            index += m_CaretColumn;
            return index;
        }

        private string GetWordBeforeCaret()
        {
            string line = m_Lines[m_CaretLine];
            int col = m_CaretColumn - 1;
            string word = "";
            while (col >= 0 && (char.IsLetterOrDigit(line[col]) || line[col] == '_'))
            {
                word = line[col] + word;
                col--;
            }
            return word;
        }

        private void EnsureCaretVisible()
        {
            if (m_CaretLine < 0 || m_CaretLine >= m_Lines.Count) return;

            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;

            int visIdx = GetVisibleLineIndex(m_CaretLine);
            if (visIdx == -1) return;

            float caretY = visIdx * LINE_HEIGHT;
            float viewportHeight = scrollView.layout.height;

            if (caretY < scrollView.scrollOffset.y)
            {
                scrollView.scrollOffset = new Vector2(scrollView.scrollOffset.x, caretY);
            }
            else if (caretY + LINE_HEIGHT > scrollView.scrollOffset.y + viewportHeight && viewportHeight > 0)
            {
                scrollView.scrollOffset = new Vector2(scrollView.scrollOffset.x, caretY + LINE_HEIGHT - viewportHeight);
            }

            int safeCol = Mathf.Clamp(m_CaretColumn, 0, m_Lines[m_CaretLine].Length);
            float caretX = LINE_NUM_WIDTH + CODE_PADDING_LEFT + GetTextWidth(m_CaretLine, safeCol);
            float viewportWidth = scrollView.layout.width;

            if (caretX < scrollView.scrollOffset.x + LINE_NUM_WIDTH)
            {
                scrollView.scrollOffset = new Vector2(Mathf.Max(0, caretX - LINE_NUM_WIDTH - CODE_PADDING_LEFT), scrollView.scrollOffset.y);
            }
            else if (caretX > scrollView.scrollOffset.x + viewportWidth && viewportWidth > 0)
            {
                scrollView.scrollOffset = new Vector2(caretX - viewportWidth + 20, scrollView.scrollOffset.y);
            }
        }

        private void UpdateCaretPosition()
        {
            RefreshScrollMarkers(); // caret line + diagnostics (analysis ends with this call)
            if (m_CaretLine < 0 || m_CaretLine >= m_Lines.Count)
            {
                m_Caret.style.display = DisplayStyle.None;
                return;
            }

            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;

            int visIdx = GetVisibleLineIndex(m_CaretLine);
            if (visIdx == -1) { m_Caret.style.display = DisplayStyle.None; return; }
            m_Caret.style.display = DisplayStyle.Flex;
            float caretY = (visIdx * LINE_HEIGHT) - scrollView.scrollOffset.y;
            int safeCol = Mathf.Clamp(m_CaretColumn, 0, m_Lines[m_CaretLine].Length);
            float caretX = LINE_NUM_WIDTH + CODE_PADDING_LEFT + GetTextWidth(m_CaretLine, safeCol) - scrollView.scrollOffset.x;

            m_Caret.style.top = caretY;
            m_Caret.style.left = caretX;

            if (caretY < -LINE_HEIGHT || caretY > layout.height)
                m_Caret.style.opacity = 0;
            else
                m_Caret.style.opacity = m_IsCaretVisible ? 1 : 0;

            UpdateSelectionVisuals();
            UpdateErrorVisuals();
            UpdateExtraCaretPositions(scrollView);
            UpdateSearchVisuals();
            UpdateDecorationVisuals();
            if (m_SignatureHelpPopup != null && m_SignatureHelpPopup.style.display == DisplayStyle.Flex)
            {
                UpdateSignatureHelpPosition();
            }
        }

        private void GetSelectionRange(out int startL, out int startC, out int endL, out int endC)
        {
            if (m_CaretLine > m_AnchorLine || (m_CaretLine == m_AnchorLine && m_CaretColumn > m_AnchorColumn))
            {
                startL = m_AnchorLine; startC = m_AnchorColumn;
                endL = m_CaretLine; endC = m_CaretColumn;
            }
            else
            {
                startL = m_CaretLine; startC = m_CaretColumn;
                endL = m_AnchorLine; endC = m_AnchorColumn;
            }
        }

        private string GetSelectedText()
        {
            if (!m_HasSelection) return "";
            GetSelectionRange(out int startL, out int startC, out int endL, out int endC);
            if (startL == endL)
            {
                return m_Lines[startL].Substring(startC, endC - startC);
            }
            else
            {
                string copied = m_Lines[startL].Substring(startC) + "\n";
                for (int i = startL + 1; i < endL; i++) copied += m_Lines[i] + "\n";
                copied += m_Lines[endL].Substring(0, endC);
                return copied;
            }
        }

        private void UpdateSelectionVisuals()
        {
            foreach (var rect in m_SelectionRects) rect.style.display = DisplayStyle.None;

            if (!m_HasSelection || (m_AnchorLine == m_CaretLine && m_AnchorColumn == m_CaretColumn)) return;

            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;
            Vector2 scroll = scrollView.scrollOffset;

            GetSelectionRange(out int startL, out int startC, out int endL, out int endC);

            int firstVisibleLine = Mathf.Max(0, Mathf.FloorToInt(scroll.y / LINE_HEIGHT) - 1);
            int lastVisibleLine = Mathf.CeilToInt((scroll.y + layout.height) / LINE_HEIGHT) + 1;

            int rectIndex = 0;

            for (int l = startL; l <= endL; l++)
            {
                if (l >= m_Lines.Count) break;

                int visIdx = GetVisibleLineIndex(l);
                if (visIdx == -1) continue;
                if (visIdx < firstVisibleLine || visIdx > lastVisibleLine) continue;

                float yPos = (visIdx * LINE_HEIGHT) - scroll.y;
                int cS = Mathf.Clamp((l == startL) ? startC : 0, 0, m_Lines[l].Length);
                int cE = Mathf.Clamp((l == endL) ? endC : m_Lines[l].Length, 0, m_Lines[l].Length);

                float startWidth = GetTextWidth(l, cS);
                float endWidth = GetTextWidth(l, cE);

                float charsWidth = endWidth - startWidth;
                if (charsWidth == 0 && l != endL)
                    charsWidth = m_CharWidth; // Highlight newline for empty lines in between

                float xPos = LINE_NUM_WIDTH + CODE_PADDING_LEFT + startWidth - scroll.x;

                VisualElement rect = GetOrCreateSelectionRect(rectIndex++);
                rect.style.top = yPos;
                rect.style.left = xPos;
                rect.style.width = charsWidth;
                rect.style.height = LINE_HEIGHT;
                rect.style.display = DisplayStyle.Flex;
            }
        }

        private VisualElement GetOrCreateSelectionRect(int index)
        {
            while (m_SelectionRects.Count <= index)
            {
                var rect = new VisualElement();
                rect.style.position = Position.Absolute;
                rect.pickingMode = PickingMode.Ignore;
                rect.style.backgroundColor = CodeIDESettings.Current.Theme.SelectionHighlight;
                m_SelectionContainer.Add(rect);
                m_SelectionRects.Add(rect);
            }
            return m_SelectionRects[index];
        }

        private void UpdateErrorVisuals()
        {
            foreach (var rect in m_ErrorRects)
                rect.style.display = DisplayStyle.None;

            if (m_LineDiagnostics == null || m_LineDiagnostics.Count == 0) return;

            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;
            Vector2 scroll = scrollView.scrollOffset;

            int firstVisibleIndex = Mathf.Max(0, (int)(scroll.y / LINE_HEIGHT));
            int lastVisibleIndex = Mathf.Min(m_VisibleLines.Count - 1, firstVisibleIndex + (int)(scrollView.layout.height / LINE_HEIGHT) + 1);

            int rectIndex = 0;

            for (int i = firstVisibleIndex; i <= lastVisibleIndex; i++)
            {
                int realLine = m_VisibleLines[i];
                if (realLine < 0 || realLine >= m_Lines.Count) continue;
                if (m_LineDiagnostics.TryGetValue(realLine, out var diags))
                {
                    foreach (var diag in diags.OrderBy(d => d.IsError ? 1 : 0))
                    {
                        float yPos = (i * LINE_HEIGHT) - scroll.y;

                        int safeStart = Mathf.Clamp(diag.LocalStart, 0, m_Lines[realLine].Length);
                        int safeEnd = Mathf.Clamp(diag.LocalEnd, safeStart, m_Lines[realLine].Length);

                        float startWidth = GetTextWidth(realLine, safeStart);
                        float endWidth = GetTextWidth(realLine, safeEnd);

                        float charsWidth = endWidth - startWidth;
                        float xPos = LINE_NUM_WIDTH + CODE_PADDING_LEFT + startWidth - scroll.x;

                        VisualElement rect = GetOrCreateErrorRect(rectIndex++);
                        rect.style.top = yPos;
                        rect.style.left = xPos;
                        rect.style.width = charsWidth;
                        rect.style.height = LINE_HEIGHT;
                    
                        // Custom hover logic instead of native tooltip
                        rect.userData = diag;
                    
                        rect.style.backgroundColor = diag.IsError ? new Color(1f, 0f, 0f, 0.25f) : new Color(1f, 0.8f, 0f, 0.25f);
                        rect.style.borderBottomWidth = 2;
                        rect.style.borderBottomColor = diag.IsError ? new Color(1f, 0.2f, 0.2f, 0.8f) : new Color(1f, 0.8f, 0.2f, 0.8f);
                        rect.style.display = DisplayStyle.Flex;
                    }
                }
            }
        }

        private VisualElement GetOrCreateErrorRect(int index)
        {
            while (m_ErrorRects.Count <= index)
            {
                var rect = new VisualElement();
                rect.style.position = Position.Absolute;
                rect.pickingMode = PickingMode.Position;

                // Forward pointer events so clicking an error still places the caret and starts selection
                rect.RegisterCallback<PointerDownEvent>(e => OnPointerDown(e));
                rect.RegisterCallback<PointerMoveEvent>(e => OnPointerMove(e));
                rect.RegisterCallback<PointerUpEvent>(e => OnPointerUp(e));
            
                rect.RegisterCallback<PointerEnterEvent>(e => {
                    m_IsHoveringDiagnostic = true;
                    m_HoverDebounceTask?.Pause();
                    m_HideHoverPopupTask?.Pause();
                    if (rect.userData is DiagnosticSpan diag)
                    {
                        m_HoverInfoPopup.Clear();
                    
                        var theme = CodeIDESettings.Current.Theme;

                        var header = new VisualElement { pickingMode = PickingMode.Ignore, style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4, flexShrink = 0 } };
                        var iconColor = diag.IsError ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 0.8f, 0.2f);
                        var icon = new Label(diag.IsError ? "✖" : "⚠") { pickingMode = PickingMode.Ignore, style = { color = iconColor, fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, marginRight = 6, flexShrink = 0 } };
                        var title = new Label(diag.IsError ? "Error" : "Warning") { pickingMode = PickingMode.Ignore, style = { color = theme.DefaultText, fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, flexShrink = 0, flexGrow = 1 } };
                        var codeId = new Label(diag.Id) { pickingMode = PickingMode.Ignore, style = { color = new Color(0.6f, 0.6f, 0.6f), fontSize = 12, marginLeft = 10, flexShrink = 0 } };
                    
                        var copyBtn = new Button() { text = "📋" };
                        copyBtn.tooltip = "Copy to clipboard";
                        copyBtn.style.paddingLeft = 4; copyBtn.style.paddingRight = 4; copyBtn.style.paddingTop = 2; copyBtn.style.paddingBottom = 2;
                        copyBtn.style.marginLeft = 6; copyBtn.style.marginRight = 0;
                        copyBtn.style.backgroundColor = Color.clear;
                        copyBtn.style.borderTopWidth = 0; copyBtn.style.borderBottomWidth = 0; copyBtn.style.borderLeftWidth = 0; copyBtn.style.borderRightWidth = 0;
                        copyBtn.style.color = new Color(0.7f, 0.7f, 0.7f);
                        copyBtn.RegisterCallback<PointerEnterEvent>(pe => {
                            m_IsHoveringDiagnostic = true;
                            m_HideHoverPopupTask?.Pause();
                            copyBtn.style.backgroundColor = theme.SelectionHighlight;
                        });
                        copyBtn.RegisterCallback<PointerLeaveEvent>(pe => copyBtn.style.backgroundColor = Color.clear);
                        copyBtn.clicked += () => {
                            UnityEditor.EditorGUIUtility.systemCopyBuffer = $"[{diag.Id}] {diag.Message}";
                            copyBtn.text = "✔";
                            copyBtn.style.color = new Color(0.2f, 0.8f, 0.2f);
                            copyBtn.schedule.Execute(() => {
                                copyBtn.text = "📋";
                                copyBtn.style.color = new Color(0.7f, 0.7f, 0.7f);
                            }).StartingIn(1000);
                        };

                        var fixBtn = new Button() { text = "✨ AI Fix" };
                        fixBtn.tooltip = "Ask AI to fix this issue";
                        fixBtn.style.paddingLeft = 6; fixBtn.style.paddingRight = 6; fixBtn.style.paddingTop = 2; fixBtn.style.paddingBottom = 2;
                        fixBtn.style.marginLeft = 6; fixBtn.style.marginRight = 0;
                        fixBtn.style.backgroundColor = new Color(0.2f, 0.4f, 0.8f);
                        fixBtn.style.borderTopWidth = 0; fixBtn.style.borderBottomWidth = 0; fixBtn.style.borderLeftWidth = 0; fixBtn.style.borderRightWidth = 0;
                        fixBtn.style.color = Color.white;
                        fixBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
                        fixBtn.RegisterCallback<PointerEnterEvent>(pe => {
                            m_IsHoveringDiagnostic = true;
                            m_HideHoverPopupTask?.Pause();
                        });
                        int lineIndex = diag.Line; 
                        fixBtn.clicked += () => {
                            string lineText = (lineIndex >= 0 && lineIndex < m_Lines.Count) ? m_Lines[lineIndex] : "";
                            OnFixErrorRequested?.Invoke($"[{diag.Id}] {diag.Message}", lineText);
                        };

                        header.Add(icon);
                        header.Add(title);
                        header.Add(codeId);
                        header.Add(copyBtn);
                        header.Add(fixBtn);

                        var msg = new Label(diag.Message) { pickingMode = PickingMode.Ignore, style = { color = theme.DefaultText, fontSize = 12, whiteSpace = WhiteSpace.Normal, minWidth = 250, maxWidth = 400, marginTop = 4, flexShrink = 0 } };
                    
                        m_HoverInfoPopup.Add(header);
                        m_HoverInfoPopup.Add(msg);

                        if (diag.Id == "CS0246" || diag.Id == "CS0103" || diag.Id == "CS0535" || diag.Id == "CS0618" || diag.Id == "CS0612" || diag.Id == "CS8019")
                        {
                            var sep = new VisualElement { pickingMode = PickingMode.Ignore, style = { height = 1, backgroundColor = theme.UIBorder, marginTop = 8, marginBottom = 8, flexShrink = 0 } };
                            var hint = new Label("💡 Right-click or Alt+Enter to view Quick Actions") { pickingMode = PickingMode.Ignore, style = { color = new Color(0.5f, 0.7f, 1f), fontSize = 12, unityFontStyleAndWeight = FontStyle.Italic, flexShrink = 0 } };
                            m_HoverInfoPopup.Add(sep);
                            m_HoverInfoPopup.Add(hint);
                        }

                        // The popup has buttons (copy, AI fix), so it must receive the pointer; it sits beside the
                        // squiggle rather than on it.
                        m_HoverInfoPopup.pickingMode = PickingMode.Position;
                        PopupPlacement.Show(m_HoverInfoPopup, rect.worldBound, preferAbove: true);
                    }
                });
            
                rect.RegisterCallback<PointerLeaveEvent>(e => {
                    m_HideHoverPopupTask?.ExecuteLater(400);
                });

                m_ErrorContainer.Add(rect);
                m_ErrorRects.Add(rect);
            }
            return m_ErrorRects[index];
        }

        private void ToggleCaretBlink()
        {
            if (m_CaretLine < 0) return;
            m_IsCaretVisible = !m_IsCaretVisible;
            UpdateCaretPosition();
        }
    }
}
