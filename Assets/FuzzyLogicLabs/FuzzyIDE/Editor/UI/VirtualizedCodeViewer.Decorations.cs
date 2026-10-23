using System.Collections.Generic;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>Caret-driven highlights drawn under the text: the matching bracket pair and other occurrences of the word.</summary>
    public partial class VirtualizedCodeViewer
    {
        private static readonly Color s_BracketBackground = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color s_BracketBorder = new Color(0.75f, 0.75f, 0.75f, 0.8f);
        private static readonly Color s_OccurrenceBackground = new Color(0.55f, 0.7f, 1f, 0.16f);

        private VisualElement m_DecorationContainer;
        private readonly List<VisualElement> m_DecorationRects = new List<VisualElement>();

        private struct Decoration
        {
            public int Line, Column, Length;
            public bool IsBracket;
        }

        private readonly List<Decoration> m_Decorations = new List<Decoration>();

        private VisualElement m_IndentGuideLayer;
        private readonly Dictionary<int, float> m_IndentWidthCache = new Dictionary<int, float>();
        private int m_IndentWidthFontSize;
        private static readonly Color s_IndentGuideColor = new Color(1f, 1f, 1f, 0.09f);

        private void InitDecorations()
        {
            m_IndentGuideLayer = new VisualElement { name = "indent-guides", pickingMode = PickingMode.Ignore };
            m_IndentGuideLayer.style.position = Position.Absolute;
            m_IndentGuideLayer.style.top = 0; m_IndentGuideLayer.style.bottom = 0;
            m_IndentGuideLayer.style.left = 0; m_IndentGuideLayer.style.right = 0;
            m_IndentGuideLayer.generateVisualContent += DrawIndentGuides;

            m_DecorationContainer = new VisualElement { name = "decoration-container", pickingMode = PickingMode.Ignore };
            m_DecorationContainer.style.position = Position.Absolute;
            m_DecorationContainer.style.top = 0; m_DecorationContainer.style.bottom = 0;
            m_DecorationContainer.style.left = 0; m_DecorationContainer.style.right = 0;
            // Below the selection so a selected range keeps its own color.
            int selectionIndex = IndexOf(m_SelectionContainer);
            if (selectionIndex >= 0) Insert(selectionIndex, m_DecorationContainer);
            else Add(m_DecorationContainer);
            Insert(IndexOf(m_DecorationContainer), m_IndentGuideLayer);
        }

        // Width of `spaces` space characters in the code font (cached per font size).
        private float IndentWidth(int spaces)
        {
            int fontSize = CodeIDESettings.Current.Editor.FontSize;
            if (fontSize != m_IndentWidthFontSize)
            {
                m_IndentWidthCache.Clear();
                m_IndentWidthFontSize = fontSize;
            }
            if (!m_IndentWidthCache.TryGetValue(spaces, out float width))
            {
                width = GetTextWidth(new string(' ', spaces));
                if (width <= 0f) return spaces * m_CharWidth; // not measurable yet (detached)
                m_IndentWidthCache[spaces] = width;
            }
            return width;
        }

        // Indentation used for guides: blank lines take the smaller indent of their nearest non-blank neighbours so a
        // block's guides don't break on empty lines.
        private int GuideIndent(int line)
        {
            string text = m_Lines[line];
            int indent = LeadingSpaces(text);
            if (indent < text.Length) return indent;

            int above = 0, below = 0;
            for (int l = line - 1; l >= 0 && l >= line - 100; l--)
            {
                string t = m_Lines[l];
                int i = LeadingSpaces(t);
                if (i < t.Length) { above = i; break; }
            }
            for (int l = line + 1; l < m_Lines.Count && l <= line + 100; l++)
            {
                string t = m_Lines[l];
                int i = LeadingSpaces(t);
                if (i < t.Length) { below = i; break; }
            }
            return Mathf.Min(above, below);
        }

        private void DrawIndentGuides(MeshGenerationContext mgc)
        {
            if (!CodeIDESettings.Current.Behaviors.ShowIndentGuides || m_Lines == null || m_ListView == null) return;
            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;

            int tab = Mathf.Max(1, CodeIDESettings.Current.Editor.TabWidth);
            Vector2 scroll = scrollView.scrollOffset;
            float codeLeft = LINE_NUM_WIDTH + CODE_PADDING_LEFT;
            float right = scrollView.layout.width;
            int firstVisible = Mathf.Max(0, (int)(scroll.y / LINE_HEIGHT));
            int lastVisible = Mathf.Min(m_VisibleLines.Count - 1, firstVisible + (int)(scrollView.layout.height / LINE_HEIGHT) + 1);

            for (int v = firstVisible; v <= lastVisible; v++)
            {
                int line = m_VisibleLines[v];
                if (line < 0 || line >= m_Lines.Count) continue;
                int levels = GuideIndent(line) / tab;
                float y = v * LINE_HEIGHT - scroll.y;
                for (int k = 0; k < levels; k++)
                {
                    float x = Mathf.Round(codeLeft + IndentWidth(k * tab) - scroll.x);
                    if (x < codeLeft - 0.5f || x > right) continue;
                    mgc.DrawSolidRect(new Rect(x, y, 1f, LINE_HEIGHT), s_IndentGuideColor);
                }
            }
        }

        private void UpdateDecorationVisuals()
        {
            if (m_DecorationContainer == null) return;
            m_IndentGuideLayer?.MarkDirtyRepaint();
            foreach (var rect in m_DecorationRects) rect.style.display = DisplayStyle.None;
            if (m_Lines == null || m_CaretLine < 0 || m_CaretLine >= m_Lines.Count) return;

            var scrollView = m_ListView.Q<ScrollView>();
            if (scrollView == null) return;

            var behaviors = CodeIDESettings.Current.Behaviors;
            m_Decorations.Clear();
            if (behaviors.HighlightMatchingBrackets) CollectBracketPair();
            if (behaviors.HighlightOccurrences) CollectOccurrences(scrollView);
            if (m_Decorations.Count == 0) return;

            Vector2 scroll = scrollView.scrollOffset;
            int firstVisible = Mathf.Max(0, (int)(scroll.y / LINE_HEIGHT));
            int lastVisible = Mathf.Min(m_VisibleLines.Count - 1, firstVisible + (int)(scrollView.layout.height / LINE_HEIGHT) + 1);

            int rectIndex = 0;
            foreach (var d in m_Decorations)
            {
                int visIdx = GetVisibleLineIndex(d.Line);
                if (visIdx < firstVisible || visIdx > lastVisible) continue;

                string text = m_Lines[d.Line];
                int start = Mathf.Clamp(d.Column, 0, text.Length);
                int end = Mathf.Clamp(d.Column + d.Length, start, text.Length);
                float startX = GetTextWidth(d.Line, start);
                float endX = GetTextWidth(d.Line, end);

                var rect = GetOrCreateDecorationRect(rectIndex++);
                rect.style.top = visIdx * LINE_HEIGHT - scroll.y;
                rect.style.left = LINE_NUM_WIDTH + CODE_PADDING_LEFT + startX - scroll.x;
                rect.style.width = Mathf.Max(1f, endX - startX);
                rect.style.height = LINE_HEIGHT;
                rect.style.backgroundColor = d.IsBracket ? s_BracketBackground : s_OccurrenceBackground;
                float border = d.IsBracket ? 1f : 0f;
                rect.style.borderTopWidth = border; rect.style.borderBottomWidth = border;
                rect.style.borderLeftWidth = border; rect.style.borderRightWidth = border;
                rect.style.display = DisplayStyle.Flex;
            }
        }

        private VisualElement GetOrCreateDecorationRect(int index)
        {
            while (m_DecorationRects.Count <= index)
            {
                var rect = new VisualElement { pickingMode = PickingMode.Ignore };
                rect.style.position = Position.Absolute;
                rect.style.borderTopColor = s_BracketBorder; rect.style.borderBottomColor = s_BracketBorder;
                rect.style.borderLeftColor = s_BracketBorder; rect.style.borderRightColor = s_BracketBorder;
                m_DecorationContainer.Add(rect);
                m_DecorationRects.Add(rect);
            }
            return m_DecorationRects[index];
        }

        private void CollectBracketPair()
        {
            if (m_HasSelection) return;
            if (BracketMatcher.TryFindMatchingBracket(m_Lines, m_CaretLine, m_CaretColumn, out int line, out int col, out int matchLine, out int matchCol))
            {
                m_Decorations.Add(new Decoration { Line = line, Column = col, Length = 1, IsBracket = true });
                m_Decorations.Add(new Decoration { Line = matchLine, Column = matchCol, Length = 1, IsBracket = true });
            }
        }

        private void CollectOccurrences(ScrollView scrollView)
        {
            string word = GetOccurrenceWord();
            if (word == null) return;

            Vector2 scroll = scrollView.scrollOffset;
            int firstVisible = Mathf.Max(0, (int)(scroll.y / LINE_HEIGHT));
            int lastVisible = Mathf.Min(m_VisibleLines.Count - 1, firstVisible + (int)(scrollView.layout.height / LINE_HEIGHT) + 1);

            int found = 0;
            int before = m_Decorations.Count;
            for (int v = firstVisible; v <= lastVisible; v++)
            {
                int line = m_VisibleLines[v];
                if (line < 0) continue;
                string text = m_Lines[line];
                int index = 0;
                while ((index = text.IndexOf(word, index, System.StringComparison.Ordinal)) >= 0)
                {
                    int end = index + word.Length;
                    bool wholeWord = (index == 0 || !IsWordChar(text[index - 1])) && (end >= text.Length || !IsWordChar(text[end]));
                    if (wholeWord)
                    {
                        m_Decorations.Add(new Decoration { Line = line, Column = index, Length = word.Length });
                        found++;
                    }
                    index = end;
                }
            }

            // A lone occurrence (the word under the caret itself) is noise.
            if (found < 2) m_Decorations.RemoveRange(before, m_Decorations.Count - before);
        }

        // The identifier under/just before the caret, or a single-line selection that is exactly one identifier.
        private string GetOccurrenceWord()
        {
            string text = m_Lines[m_CaretLine];
            if (m_HasSelection)
            {
                GetSelectionRange(out int sL, out int sC, out int eL, out int eC);
                if (sL != eL || eC <= sC) return null;
                string selected = text.Substring(sC, Mathf.Min(eC, text.Length) - sC);
                foreach (char ch in selected) if (!IsWordChar(ch)) return null;
                return selected.Length >= 2 ? selected : null;
            }

            int col = Mathf.Clamp(m_CaretColumn, 0, text.Length);
            int start = col, end = col;
            while (start > 0 && IsWordChar(text[start - 1])) start--;
            while (end < text.Length && IsWordChar(text[end])) end++;
            if (end - start < 2 || char.IsDigit(text[start])) return null;
            if (!BracketMatcher.IsCode(text, start)) return null;
            return text.Substring(start, end - start);
        }

        private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
    }
}
