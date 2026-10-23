using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>
    /// Overview ruler drawn on the vertical scrollbar track: errors and warnings (right), search matches (middle),
    /// git changes (left) and the caret line, each at its relative position in the file.
    /// </summary>
    public partial class VirtualizedCodeViewer
    {
        private VisualElement m_ScrollMarkers;
        private static readonly Color s_MarkerError = new Color(1f, 0.3f, 0.3f, 0.95f);
        private static readonly Color s_MarkerWarning = new Color(1f, 0.8f, 0.2f, 0.9f);
        private static readonly Color s_MarkerSearch = new Color(1f, 0.6f, 0.15f, 0.9f);
        private static readonly Color s_MarkerCaret = new Color(0.85f, 0.85f, 0.85f, 0.9f);

        /// <summary>Attaches the ruler to the scrollbar track; the ScrollView exists once the ListView is laid out.</summary>
        private void InitScrollMarkers()
        {
            var scroller = m_ListView.Q<ScrollView>()?.verticalScroller;
            if (scroller == null || m_ScrollMarkers != null) return;
            var track = (VisualElement)scroller.slider ?? scroller;
            m_ScrollMarkers = new VisualElement { name = "scroll-markers", pickingMode = PickingMode.Ignore };
            m_ScrollMarkers.style.position = Position.Absolute;
            m_ScrollMarkers.style.left = 0; m_ScrollMarkers.style.right = 0; m_ScrollMarkers.style.top = 0; m_ScrollMarkers.style.bottom = 0;
            m_ScrollMarkers.generateVisualContent += DrawScrollMarkers;
            track.Add(m_ScrollMarkers);
        }

        private void RefreshScrollMarkers() => m_ScrollMarkers?.MarkDirtyRepaint();

        private void DrawScrollMarkers(MeshGenerationContext mgc)
        {
            var rect = m_ScrollMarkers.contentRect;
            int total = m_VisibleLines.Count;
            if (total == 0 || rect.height <= 0 || m_Lines == null) return;
            float width = rect.width;
            float markHeight = Mathf.Max(2f, Mathf.Min(4f, rect.height / total));

            float Y(int line)
            {
                int visIdx = GetVisibleLineIndex(line);
                if (visIdx < 0) return -1f;
                return Mathf.Min(rect.height - markHeight, visIdx / (float)total * rect.height);
            }

            var git = m_GitLineDiffResult;
            if (git != null && CodeIDESettings.Current.Git.EnableGitIntegration)
            {
                var theme = CodeIDESettings.Current.Theme;
                foreach (var kvp in git.LineStatuses)
                {
                    float y = Y(kvp.Key);
                    if (y < 0) continue;
                    var color = kvp.Value == Git.GitLineDiffType.Added ? theme.GitAdded : kvp.Value == Git.GitLineDiffType.Modified ? theme.GitModified : theme.GitDeleted;
                    mgc.DrawSolidRect(new Rect(0, y, width * 0.3f, markHeight), color);
                }
            }

            foreach (var match in m_SearchMatches)
            {
                float y = Y(match.Line);
                if (y >= 0) mgc.DrawSolidRect(new Rect(width * 0.3f, y, width * 0.35f, markHeight), s_MarkerSearch);
            }

            if (m_LineDiagnostics != null)
            {
                foreach (var kvp in m_LineDiagnostics)
                {
                    if (kvp.Value == null || kvp.Value.Count == 0) continue;
                    float y = Y(kvp.Key);
                    if (y < 0) continue;
                    mgc.DrawSolidRect(new Rect(width * 0.65f, y, width * 0.35f, markHeight), kvp.Value.Exists(d => d.IsError) ? s_MarkerError : s_MarkerWarning);
                }
            }

            float caretY = Y(m_CaretLine);
            if (caretY >= 0) mgc.DrawSolidRect(new Rect(0, caretY, width, 1.5f), s_MarkerCaret);
        }
    }
}
