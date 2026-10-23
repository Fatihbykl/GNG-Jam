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
        private static readonly Unity.Profiling.ProfilerMarker s_BindItemMarker = new Unity.Profiling.ProfilerMarker("FuzzyIDE.CodeViewer.BindItem");

        private string GetFormattedLinePrefix(int lineIndex, int length)
        {
            if (lineIndex < 0 || lineIndex >= m_Lines.Count || length <= 0) return "";
            string rawText = m_Lines[lineIndex];
            if (length > rawText.Length) length = rawText.Length;
            m_LineHighlights.TryGetValue(lineIndex, out var spans);
            // Same markup as the rendered row (inlay hints included), so widths measured from it line up with the text.
            return ComposeMarkup(rawText, spans, GetInlays(lineIndex, rawText), length, false);
        }

        private struct LineInlay
        {
            public int Col;
            public string Markup;
        }

        private sealed class LineInlays
        {
            public string Raw; // the line text the hints were computed for
            public List<LineInlay> Items;
        }

        private Dictionary<int, LineInlays> m_LineInlays = new Dictionary<int, LineInlays>();

        // Hints computed for an older version of the line are dropped until the next analysis.
        private List<LineInlay> GetInlays(int lineIndex, string rawText)
        {
            return m_LineInlays.TryGetValue(lineIndex, out var inlays) && ReferenceEquals(inlays.Raw, rawText) ? inlays.Items : null;
        }

        private void ApplyInlayHints(List<InlayHint> hints, int[] lineOffsets)
        {
            var result = new Dictionary<int, LineInlays>();
            string color = "#" + ColorUtility.ToHtmlStringRGBA(CodeIDESettings.Current.Theme.InlayHintColor);
            foreach (var hint in hints)
            {
                int line = FindLineIndex(lineOffsets, hint.Position);
                if (line < 0 || line >= m_Lines.Count) continue;
                int col = hint.Position - lineOffsets[line];
                if (col < 0 || col > m_Lines[line].Length) continue;
                if (!result.TryGetValue(line, out var inlays)) result[line] = inlays = new LineInlays { Raw = m_Lines[line], Items = new List<LineInlay>() };
                string text = hint.Text.EndsWith(":") ? hint.Text + " " : hint.Text;
                inlays.Items.Add(new LineInlay { Col = col, Markup = $"<color={color}><i>{text.Replace("<", "<\u200B")}</i></color>" });
            }
            m_LineInlays = result;
        }

        private static string Escape(string raw, int from, int to) => to > from ? raw.Substring(from, to - from).Replace("<", "<\u200B") : "";

        // Raw text [from, to) with the inlay hints that fall inside it.
        private static void AppendRaw(System.Text.StringBuilder sb, string raw, int from, int to, List<LineInlay> inlays, ref int next)
        {
            int pos = from;
            while (inlays != null && next < inlays.Count && inlays[next].Col < to)
            {
                int col = Mathf.Max(inlays[next].Col, pos);
                sb.Append(Escape(raw, pos, col)).Append(inlays[next].Markup);
                pos = col;
                next++;
            }
            sb.Append(Escape(raw, pos, to));
        }

        /// <summary>Rich text for the first <paramref name="length"/> characters of a line: highlight spans plus inlay hints.</summary>
        private static string ComposeMarkup(string raw, List<SyntaxSpan> spans, List<LineInlay> inlays, int length, bool includeEndInlays)
        {
            bool hasSpans = spans != null && spans.Count > 0;
            bool hasInlays = inlays != null && inlays.Count > 0;
            if (!hasSpans && !hasInlays) return Escape(raw, 0, length);

            var sb = new System.Text.StringBuilder(length + 64);
            int next = 0, current = 0;
            if (hasSpans)
            {
                foreach (var span in spans)
                {
                    if (span.StartIndex >= length) break;
                    int safeStart = Mathf.Clamp(span.StartIndex, current, length);
                    int safeEnd = Mathf.Clamp(span.StartIndex + span.Length, safeStart, length);
                    if (safeStart > current) AppendRaw(sb, raw, current, safeStart, inlays, ref next);
                    if (safeEnd > safeStart)
                    {
                        if (span.IsBold) sb.Append("<b>");
                        if (span.IsItalic) sb.Append("<i>");
                        if (!string.IsNullOrEmpty(span.ColorHex)) sb.Append("<color=").Append(span.ColorHex).Append('>');
                        AppendRaw(sb, raw, safeStart, safeEnd, inlays, ref next);
                        if (!string.IsNullOrEmpty(span.ColorHex)) sb.Append("</color>");
                        if (span.IsItalic) sb.Append("</i>");
                        if (span.IsBold) sb.Append("</b>");
                    }
                    current = safeEnd;
                }
            }
            if (current < length) AppendRaw(sb, raw, current, length, inlays, ref next);
            if (includeEndInlays)
            {
                while (inlays != null && next < inlays.Count && inlays[next].Col <= length) sb.Append(inlays[next++].Markup);
            }
            return sb.ToString();
        }

        private float GetTextWidth(int lineIndex, int length)
        {
            if (lineIndex < 0 || lineIndex >= m_Lines.Count || length <= 0) return 0f;
            string rawText = m_Lines[lineIndex];
            if (length > rawText.Length) length = rawText.Length;

            string formattedText = GetFormattedLinePrefix(lineIndex, length);
            if (string.IsNullOrEmpty(formattedText)) return 0f;

            if (rawText[length - 1] == ' ')
            {
                float widthWithX = m_MeasureLabel.MeasureTextSize(formattedText + "X", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
                float widthOfX = m_MeasureLabel.MeasureTextSize("X", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
                return widthWithX - widthOfX;
            }

            return m_MeasureLabel.MeasureTextSize(formattedText, 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
        }

        private float GetTextWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            // Unity's MeasureTextSize strips trailing spaces.
            // Appending 'X' and subtracting its width ensures we measure the exact physical width 
            // of the native space character without triggering font-fallback width mismatches.
            if (text[text.Length - 1] == ' ')
            {
                float widthWithX = m_MeasureLabel.MeasureTextSize(text + "X", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
                float widthOfX = m_MeasureLabel.MeasureTextSize("X", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
                return widthWithX - widthOfX;
            }

            return m_MeasureLabel.MeasureTextSize(text, 0, MeasureMode.Undefined, 0, MeasureMode.Undefined).x;
        }

        // -----------------------
        // ListView Virtualization Core
        // -----------------------
        private VisualElement MakeItem()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.height = LINE_HEIGHT;
            row.style.flexShrink = 0;

            var gutterContainer = new VisualElement();
            gutterContainer.name = "gutterContainer";
            gutterContainer.style.flexDirection = FlexDirection.Row;
            gutterContainer.style.width = LINE_NUM_WIDTH;
            gutterContainer.style.flexShrink = 0;
            gutterContainer.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);

            var lineNumLabel = new Label();
            lineNumLabel.name = "lineNum";
            lineNumLabel.style.flexGrow = 1;
            lineNumLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            lineNumLabel.style.paddingRight = 4;
            lineNumLabel.style.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            lineNumLabel.style.fontSize = 14; 

            var foldToggle = new Label();
            foldToggle.name = "foldToggle";
            foldToggle.style.width = 16;
            foldToggle.style.unityTextAlign = TextAnchor.MiddleCenter;
            foldToggle.style.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            foldToggle.style.fontSize = 11;
            foldToggle.style.unityFontStyleAndWeight = FontStyle.Bold;
            
            foldToggle.RegisterCallback<MouseEnterEvent>(e => {
                if (foldToggle.style.visibility == Visibility.Visible)
                    foldToggle.style.color = new Color(1f, 1f, 1f, 1f);
            });
            foldToggle.RegisterCallback<MouseLeaveEvent>(e => {
                foldToggle.style.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            });
        
            var gitMarker = new VisualElement();
            gitMarker.name = "gitMarker";
            gitMarker.style.width = 3;
            gitMarker.style.height = Length.Percent(100);
            gitMarker.style.flexShrink = 0;
            gitMarker.style.backgroundColor = Color.clear;
            gitMarker.RegisterCallback<PointerDownEvent>(OnGitMarkerPointerDown);
            gutterContainer.Add(gitMarker);
            gutterContainer.Add(CreateNoteMarker());

            gutterContainer.Add(lineNumLabel);
            gutterContainer.Add(foldToggle);

            var codeLabel = new Label();
            codeLabel.name = "code";
            codeLabel.style.flexShrink = 0;
            codeLabel.style.flexGrow = 0;
            codeLabel.style.paddingLeft = CODE_PADDING_LEFT;
            codeLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            codeLabel.style.whiteSpace = WhiteSpace.Pre; // Preserves multiple spaces perfectly!
            codeLabel.style.color = new Color(0.85f, 0.85f, 0.85f, 1f);
            codeLabel.style.fontSize = 14; // Enforce exact font size
            codeLabel.enableRichText = true; // Crucial for syntax highlighting

            if (m_MonospaceFont != null)
            {
                lineNumLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
                codeLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
            }

            row.Add(gutterContainer);
            row.Add(codeLabel);
            row.Add(CreateInlineHintLabel());

            return row;
        }

        private void BindItem(VisualElement element, int index)
        {
            using (s_BindItemMarker.Auto()) BindItemCore(element, index);
        }

        private void BindItemCore(VisualElement element, int index)
        {
            var gutterContainer = element.Q<VisualElement>("gutterContainer");
            var lineNumLabel = element.Q<Label>("lineNum");
            var foldToggle = element.Q<Label>("foldToggle");
            var codeLabel = element.Q<Label>("code");
        
            if (m_MonospaceFont != null)
            {
                lineNumLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
                codeLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
            }

            if (index < 0 || index >= m_VisibleLines.Count) return;
            int realIndex = m_VisibleLines[index];
            if (realIndex >= m_Lines.Count)
            {
                // Stale row from before the text shrank; the refresh rebinds it.
                lineNumLabel.text = string.Empty;
                codeLabel.text = string.Empty;
                schedule.Execute(RefreshAfterSharedEdit);
                return;
            }
            BindInlineHint(element, realIndex);

            var theme = CodeIDESettings.Current.Theme;
            int fontSize = CodeIDESettings.Current.Editor.FontSize;

            if (realIndex < 0)
            {
                int delIndex = (-realIndex) - 1;
                element.style.height = LINE_HEIGHT;
                element.style.backgroundColor = new Color(0.5f, 0.1f, 0.1f, 0.3f);
                gutterContainer.style.backgroundColor = new Color(0.6f, 0.1f, 0.1f, 0.8f);
                lineNumLabel.style.borderLeftWidth = 2;
                lineNumLabel.style.fontSize = fontSize;
                lineNumLabel.text = "-";
                lineNumLabel.style.color = new Color(1f, 0.3f, 0.3f);
                codeLabel.style.fontSize = fontSize;
                codeLabel.style.color = new Color(1f, 0.5f, 0.5f);
                codeLabel.text = m_PendingDiff.DeletedLines[delIndex];
                foldToggle.style.visibility = Visibility.Hidden;
                if (m_MonospaceFont != null)
                {
                    lineNumLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
                    codeLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
                }
                return;
            }

            element.style.height = LINE_HEIGHT;

            var gitMarker = element.Q<VisualElement>("gitMarker");
            if (gitMarker != null)
            {
                gitMarker.userData = realIndex;
                gitMarker.style.borderTopWidth = 0;
                gitMarker.style.borderTopColor = Color.clear;
                gitMarker.tooltip = "";

                if (CodeIDESettings.Current.Git.ShowGutterDiffMarkers && m_GitLineDiffResult != null &&
                    m_GitLineDiffResult.LineStatuses.TryGetValue(realIndex, out var gitType))
                {
                    if (gitType == Git.GitLineDiffType.Added)
                    {
                        gitMarker.style.backgroundColor = theme.GitAdded;
                        gitMarker.tooltip = "Git: Added line (Click to peek diff)";
                    }
                    else if (gitType == Git.GitLineDiffType.Modified)
                    {
                        gitMarker.style.backgroundColor = theme.GitModified;
                        gitMarker.tooltip = "Git: Modified line (Click to peek diff)";
                    }
                    else if (gitType == Git.GitLineDiffType.DeletedAbove)
                    {
                        gitMarker.style.backgroundColor = Color.clear;
                        gitMarker.style.borderTopWidth = 3;
                        gitMarker.style.borderTopColor = theme.GitDeleted;
                        gitMarker.tooltip = "Git: Deleted line(s) above (Click to view)";
                    }
                    else
                    {
                        gitMarker.style.backgroundColor = Color.clear;
                    }
                }
                else
                {
                    gitMarker.style.backgroundColor = Color.clear;
                }
            }
            
            BindNoteMarker(element, realIndex);

            if (m_PendingDiff != null && realIndex >= m_PendingDiff.StartLine && realIndex < m_PendingDiff.StartLine + m_PendingDiff.AddedLinesCount)
            {
                element.style.backgroundColor = new Color(0.1f, 0.5f, 0.1f, 0.3f);
                gutterContainer.style.backgroundColor = new Color(0.1f, 0.6f, 0.1f, 0.8f);
            }
            else
            {
                element.style.backgroundColor = (realIndex == m_CaretLine || IsExtraCaretLine(realIndex)) ? theme.ActiveLineBackground : Color.clear;
                gutterContainer.style.backgroundColor = theme.GutterBackground;
            }
            lineNumLabel.style.borderLeftWidth = 2;
            lineNumLabel.style.fontSize = fontSize;
            codeLabel.style.fontSize = fontSize;
            codeLabel.style.color = theme.DefaultText;
        
            foldToggle.UnregisterCallback<PointerDownEvent>(ToggleFoldEvent);
            foldToggle.userData = realIndex;
            foldToggle.RegisterCallback<PointerDownEvent>(ToggleFoldEvent);

            bool isFoldable = false;
            bool isFolded = false;
            if (m_FoldableBlockMap.TryGetValue(realIndex, out var b))
            {
                isFoldable = true;
                isFolded = b.IsFolded;
            }
            if (isFoldable)
            {
                foldToggle.text = isFolded ? "▶" : "▼";
                foldToggle.style.visibility = Visibility.Visible;
            }
            else
            {
                foldToggle.text = "";
                foldToggle.style.visibility = Visibility.Hidden;
            }


            if (m_MonospaceFont != null)
            {
                lineNumLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
                codeLabel.style.unityFontDefinition = new StyleFontDefinition(m_MonospaceFont);
            }

            lineNumLabel.text = (realIndex + 1).ToString();

            if (m_LineDiagnostics.TryGetValue(realIndex, out var diags) && diags.Count > 0)
            {
                bool hasError = diags.Exists(d => d.IsError);
                lineNumLabel.style.color = hasError ? new Color(1f, 0.3f, 0.3f) : new Color(1f, 0.8f, 0.2f);
                lineNumLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            }
            else
            {
                lineNumLabel.style.color = (realIndex == m_CaretLine) ? Color.white : new Color(0.52f, 0.52f, 0.52f);
                lineNumLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
            }

            if (realIndex == m_CaretLine)
            {
                lineNumLabel.style.borderLeftColor = new Color(0f, 0.478f, 0.8f); // #007ACC
            }
            else
            {
                lineNumLabel.style.borderLeftColor = Color.clear;
            }

            string rawText = m_Lines[realIndex];

            m_LineHighlights.TryGetValue(realIndex, out var spans);
            codeLabel.text = GetLineMarkup(realIndex, rawText, spans, isFolded);


        
            string trackId = null;
            m_LiveBadges.TryGetValue(realIndex, out trackId);
        
            var liveBadge = element.Q<Label>("liveValueBadge");
            if (liveBadge == null)
            {
                liveBadge = new Label();
                liveBadge.name = "liveValueBadge";
                liveBadge.style.position = Position.Relative;
                liveBadge.style.marginLeft = 15;
                liveBadge.style.alignSelf = Align.Center;
                liveBadge.style.paddingLeft = 8;
                liveBadge.style.paddingRight = 8;
                liveBadge.style.paddingTop = 2;
                liveBadge.style.paddingBottom = 2;
                liveBadge.style.borderTopLeftRadius = 6;
                liveBadge.style.borderTopRightRadius = 6;
                liveBadge.style.borderBottomLeftRadius = 6;
                liveBadge.style.borderBottomRightRadius = 6;
                liveBadge.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
                liveBadge.style.color = new Color(0f, 1f, 0.5f); // Neon green
                liveBadge.style.fontSize = 11;
                element.Add(liveBadge);
            }

            liveBadge.userData = trackId;

            // Note: checking FuzzyLogicLabs namespace. Requires assembly access or reflection if not referenced, but we can assume it's referenced since IDE compiles with user code or uses reflection?
            // Wait, VirtualizedCodeViewer is in Editor assembly. FuzzyLogicLabs.LiveStateTracker is in Runtime assembly.
            // The Editor assembly typically references Runtime assembly automatically in Unity.
            // We will just use Type.GetType if it's not referenced directly, but we can try direct access.
            var liveState = GetLiveStateTracker();
            if (UnityEditor.EditorApplication.isPlaying && trackId != null && liveState != null && liveState.Contains(trackId))
            {
                object ctx = liveState[trackId];
                string liveValue = (string)ctx.GetType().GetField("CurrentValue").GetValue(ctx);
                liveBadge.text = liveValue;
                liveBadge.style.display = DisplayStyle.Flex;
            }
            else
            {
                liveBadge.style.display = DisplayStyle.None;
            }
        }

        private struct LineMarkup
        {
            public string Raw;
            public List<SyntaxSpan> Spans;
            public List<LineInlay> Inlays;
            public bool Folded;
            public string Markup;
        }

        // Rich-text markup per line, reused while the line text (strings are replaced on edit) and its highlight list
        // (replaced wholesale by each analysis) are unchanged, so rebinding after analysis or scrolling skips the rebuild.
        private readonly Dictionary<int, LineMarkup> m_LineMarkupCache = new Dictionary<int, LineMarkup>();

        private string GetLineMarkup(int lineIndex, string rawText, List<SyntaxSpan> spans, bool isFolded)
        {
            var inlays = GetInlays(lineIndex, rawText);
            if (m_LineMarkupCache.TryGetValue(lineIndex, out var cached) && ReferenceEquals(cached.Raw, rawText) &&
                ReferenceEquals(cached.Spans, spans) && ReferenceEquals(cached.Inlays, inlays) && cached.Folded == isFolded)
            {
                return cached.Markup;
            }

            string markup = ComposeMarkup(rawText, spans, inlays, rawText.Length, true) + (isFolded ? " <color=#888>...</color>" : "");
            m_LineMarkupCache[lineIndex] = new LineMarkup { Raw = rawText, Spans = spans, Inlays = inlays, Folded = isFolded, Markup = markup };
            return markup;
        }

        private static System.Collections.IDictionary s_LiveStateDictionary;
        private static bool s_TriedToGetLiveState = false;

        private static System.Collections.IDictionary GetLiveStateTracker()
        {
            if (s_LiveStateDictionary != null) return s_LiveStateDictionary;
            if (s_TriedToGetLiveState) return null;
            s_TriedToGetLiveState = true;
        
            var assemblies = UnityCompat.GetLoadedAssemblies();
            foreach (var asm in assemblies)
            {
                var type = asm.GetType("FuzzyLogicLabs.LiveStateTracker");
                if (type != null)
                {
                    var field = type.GetField("State", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (field != null)
                    {
                        s_LiveStateDictionary = field.GetValue(null) as System.Collections.IDictionary;
                        return s_LiveStateDictionary;
                    }
                }
            }
            return null;
        }

        public bool UpdateLiveBadges()
        {
            if (!UnityEditor.EditorApplication.isPlaying || m_ListView == null) return false;
        
            var liveState = GetLiveStateTracker();
            if (liveState == null) return false;
        
            bool changed = false;
            m_ListView.Query<Label>("liveValueBadge").ForEach(badge => 
            {
                if (badge.userData is string trackId)
                {
                    if (liveState.Contains(trackId))
                    {
                        object ctx = liveState[trackId];
                        string liveValue = (string)ctx.GetType().GetField("CurrentValue").GetValue(ctx);
                        if (badge.text != liveValue) 
                        {
                            badge.text = liveValue;
                            changed = true;
                        }
                        if (badge.style.display == DisplayStyle.None) 
                        {
                            badge.style.display = DisplayStyle.Flex;
                            changed = true;
                        }
                    }
                    else
                    {
                        if (badge.style.display == DisplayStyle.Flex) 
                        {
                            badge.style.display = DisplayStyle.None;
                            changed = true;
                        }
                    }
                }
            });
            return changed;
        }

        private void ToggleFoldEvent(PointerDownEvent evt)
        {
            if (evt.currentTarget is Label foldToggle && foldToggle.userData is int realIndex)
            {
                for (int i = 0; i < m_FoldableBlocks.Count; i++)
                {
                    var block = m_FoldableBlocks[i];
                    if (block.StartLine == realIndex)
                    {
                        block.IsFolded = !block.IsFolded;
                        m_FoldableBlocks[i] = block;
                        m_FoldableBlockMap[block.StartLine] = block;
                        RecalculateVisibleLines();
                        break;
                    }
                }
            }
        }

        private void UnbindItem(VisualElement element, int index)
        {
            var gitMarker = element.Q<VisualElement>("gitMarker");
            if (gitMarker != null)
            {
                gitMarker.style.backgroundColor = Color.clear;
                gitMarker.style.borderTopWidth = 0;
                gitMarker.tooltip = "";
            }
            var foldToggle = element.Q<Label>("foldToggle");
            if (foldToggle != null) foldToggle.UnregisterCallback<PointerDownEvent>(ToggleFoldEvent);
            var lineNumLabel = element.Q<Label>("lineNum");
            var codeLabel = element.Q<Label>("code");
            lineNumLabel.text = string.Empty;
            codeLabel.text = string.Empty;
        }
    }
}
