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
        private void HandleHoverInfo(PointerMoveEvent evt)
        {
            if (m_IsDragging || (m_ContextMenuPopup != null && m_ContextMenuPopup.style.display == DisplayStyle.Flex) || LanguageService == null)
            {
                HideHoverInfo();
                return;
            }

            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;

            Vector2 localPos = m_ListView.WorldToLocal(evt.position);
            GetLineAndCol(localPos, scrollView, out int line, out int col);

            if (m_IsHoveringDiagnostic || HasDiagnosticAt(line, col))
            {
                m_HoverDebounceTask?.Pause();
                return;
            }

            if (line < 0 || line >= m_Lines.Count)
            {
                HideHoverInfo();
                return;
            }

            string lineText = m_Lines[line];
            if (col < 0 || col >= lineText.Length || (!char.IsLetterOrDigit(lineText[col]) && lineText[col] != '_'))
            {
                HideHoverInfo();
                return;
            }

            int absPos = 0;
            for (int i = 0; i < line; i++) absPos += m_Lines[i].Length + 1;
            absPos += col;

            if (absPos == m_LastHoverPos && m_HoverInfoPopup.style.display == DisplayStyle.Flex)
            {
                return;
            }

            m_LastHoverPos = absPos;
            m_HoverDebounceTask?.Pause();

            // Anchor on the whole hovered word, in the same row coordinates the caret overlay uses.
            int wordStart = col, wordEnd = col;
            while (wordStart > 0 && (char.IsLetterOrDigit(lineText[wordStart - 1]) || lineText[wordStart - 1] == '_')) wordStart--;
            while (wordEnd < lineText.Length && (char.IsLetterOrDigit(lineText[wordEnd]) || lineText[wordEnd] == '_')) wordEnd++;
            int visIdx = GetVisibleLineIndex(line);
            float lineTop = visIdx >= 0 ? (visIdx * LINE_HEIGHT) - scrollView.scrollOffset.y : 0;
            float textLeft = LINE_NUM_WIDTH + CODE_PADDING_LEFT - scrollView.scrollOffset.x;
            float wordLeft = textLeft + GetTextWidth(line, wordStart);
            float wordRight = textLeft + GetTextWidth(line, wordEnd);

            m_HoverDebounceTask = schedule.Execute(async () =>
            {
                string fullCode = string.Join("\n", m_Lines);
                var hoverInfo = await LanguageService.GetHoverInfoAsync(fullCode, absPos);

                if (m_IsHoveringDiagnostic || HasDiagnosticAt(line, col)) return;

                if (hoverInfo != null && !string.IsNullOrEmpty(hoverInfo.DisplayString))
                {
                    m_HoverInfoPopup.Clear();
                    var theme = CodeIDESettings.Current.Theme;

                    var categoryLabel = new Label($"[{hoverInfo.Category ?? "Symbol"}] {hoverInfo.SymbolName ?? ""}")
                    {
                        pickingMode = PickingMode.Ignore,
                        style =
                        {
                            color = new Color(0.4f, 0.7f, 1.0f),
                            fontSize = 11,
                            unityFontStyleAndWeight = FontStyle.Bold,
                            marginBottom = 2
                        }
                    };

                    var displayLabel = new Label(hoverInfo.DisplayString)
                    {
                        pickingMode = PickingMode.Ignore,
                        style =
                        {
                            color = theme.DefaultText,
                            fontSize = 12,
                            unityFontStyleAndWeight = FontStyle.Normal,
                            whiteSpace = WhiteSpace.Normal
                        }
                    };

                    m_HoverInfoPopup.Add(categoryLabel);
                    m_HoverInfoPopup.Add(displayLabel);

                    if (!string.IsNullOrEmpty(hoverInfo.LocationInfo))
                    {
                        var locLabel = new Label($"In: {hoverInfo.LocationInfo}")
                        {
                            pickingMode = PickingMode.Ignore,
                            style =
                            {
                                color = new Color(0.6f, 0.6f, 0.65f),
                                fontSize = 11,
                                unityFontStyleAndWeight = FontStyle.Italic,
                                marginTop = 2
                            }
                        };
                        m_HoverInfoPopup.Add(locLabel);
                    }

                    if (!string.IsNullOrEmpty(hoverInfo.LiveValue))
                    {
                        var liveLabel = new Label($"⚡ Live Value: {hoverInfo.LiveValue}")
                        {
                            pickingMode = PickingMode.Ignore,
                            style =
                            {
                                color = new Color(0.4f, 0.95f, 0.4f),
                                fontSize = 11,
                                unityFontStyleAndWeight = FontStyle.Bold,
                                marginTop = 4
                            }
                        };
                        m_HoverInfoPopup.Add(liveLabel);
                    }

                    if (!string.IsNullOrEmpty(hoverInfo.Documentation))
                    {
                        var docLabel = new Label(hoverInfo.Documentation)
                        {
                            pickingMode = PickingMode.Ignore,
                            style =
                            {
                                color = new Color(0.75f, 0.75f, 0.75f),
                                fontSize = 11,
                                marginTop = 4,
                                whiteSpace = WhiteSpace.Normal
                            }
                        };
                        m_HoverInfoPopup.Add(docLabel);
                    }

                    var footerLabel = new Label("F12: Go to Def | Right-Click: Watch Live")
                    {
                        pickingMode = PickingMode.Ignore,
                        style =
                        {
                            color = new Color(0.5f, 0.5f, 0.55f),
                            fontSize = 10,
                            marginTop = 6,
                            borderTopWidth = 1,
                            borderTopColor = new Color(0.25f, 0.25f, 0.28f),
                            paddingTop = 3
                        }
                    };
                    m_HoverInfoPopup.Add(footerLabel);

                    // Symbol info has nothing to click: let clicks reach the text underneath.
                    m_HoverInfoPopup.pickingMode = PickingMode.Ignore;
                    PopupPlacement.Show(m_HoverInfoPopup, PopupPlacement.LineAnchor(this, wordLeft, wordRight, lineTop, LINE_HEIGHT), preferAbove: true);
                }
            }).StartingIn(500);
        }

        private bool HasDiagnosticAt(int line, int col)
        {
            if (m_LineDiagnostics != null && m_LineDiagnostics.TryGetValue(line, out var diagList) && diagList != null)
            {
                for (int i = 0; i < diagList.Count; i++)
                {
                    if (col >= diagList[i].LocalStart && col <= diagList[i].LocalEnd)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Closes hover and diagnostic popups right away (typing, scrolling, clicking in the text).</summary>
        private void DismissHoverPopups()
        {
            m_LastHoverPos = -1;
            m_HoverDebounceTask?.Pause();
            m_HideHoverPopupTask?.Pause();
            m_IsHoveringDiagnostic = false;
            if (m_HoverInfoPopup != null && m_HoverInfoPopup.style.display != DisplayStyle.None)
                m_HoverInfoPopup.style.display = DisplayStyle.None;
        }

        private void HideHoverInfo()
        {
            m_LastHoverPos = -1;
            m_HoverDebounceTask?.Pause();
            if (m_IsHoveringDiagnostic) return;
            if (m_HoverInfoPopup != null)
            {
                m_HoverInfoPopup.style.display = DisplayStyle.None;
            }
        }
    }
}
