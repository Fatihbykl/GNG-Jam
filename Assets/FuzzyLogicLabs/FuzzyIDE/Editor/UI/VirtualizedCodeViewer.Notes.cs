using System;
using System.Collections.Generic;
using System.Text;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>Gutter markers for Fuzzy Notes that link to lines or symbols of the open file.</summary>
    public partial class VirtualizedCodeViewer
    {
        private const float NOTE_MARKER_WIDTH = 12f;

        private Dictionary<int, List<NoteRefHit>> m_NoteMarkers = new Dictionary<int, List<NoteRefHit>>();
        private IVisualElementScheduledItem m_NoteMarkerTask;
        private string m_NoteMarkerPath;
        private int m_NoteMarkerIndexVersion = -1;

        private void InitNoteMarkers()
        {
            OnTextChanged += () => ScheduleNoteMarkers(false);

            Action onNotesChanged = () => ScheduleNoteMarkers(true);
            Action<NoteItem> onNoteSaved = _ => ScheduleNoteMarkers(false);
            RegisterCallback<AttachToPanelEvent>(e =>
            {
                NoteStore.OnNotesChanged += onNotesChanged;
                NoteStore.OnNoteSaved += onNoteSaved;
                NoteStore.OnNoteReloaded += onNoteSaved;
                ScheduleNoteMarkers(true);
            });
            RegisterCallback<DetachFromPanelEvent>(e =>
            {
                NoteStore.OnNotesChanged -= onNotesChanged;
                NoteStore.OnNoteSaved -= onNoteSaved;
                NoteStore.OnNoteReloaded -= onNoteSaved;
                m_NoteMarkerTask?.Pause();
            });
        }

        /// <summary>Recomputes note markers; text edits are debounced because symbol anchors need a parse.</summary>
        public void ScheduleNoteMarkers(bool immediate)
        {
            if (panel == null) return;
            m_NoteMarkerTask?.Pause();
            m_NoteMarkerTask = schedule.Execute(RefreshNoteMarkers).StartingIn(immediate ? 0 : 600);
        }

        private void RefreshNoteMarkers()
        {
            string path = CurrentFilePath;
            Dictionary<int, List<NoteRefHit>> markers;
            if (string.IsNullOrEmpty(path))
            {
                markers = new Dictionary<int, List<NoteRefHit>>();
            }
            else
            {
                try { markers = CodeNotes.GetLineMarkers(path, m_Lines); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FuzzyIDE] Note markers unavailable: {ex.Message}");
                    markers = new Dictionary<int, List<NoteRefHit>>();
                }
            }

            bool changed = path != m_NoteMarkerPath || !SameMarkers(markers, m_NoteMarkers) ||
                           m_NoteMarkerIndexVersion != NoteStore.Instance.Index.Version;
            m_NoteMarkerPath = path;
            m_NoteMarkerIndexVersion = NoteStore.Instance.Index.Version;
            m_NoteMarkers = markers;
            if (changed) m_ListView?.RefreshItems();
        }

        private static bool SameMarkers(Dictionary<int, List<NoteRefHit>> a, Dictionary<int, List<NoteRefHit>> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var other) || other.Count != kv.Value.Count) return false;
                for (int i = 0; i < other.Count; i++)
                    if (other[i].Note != kv.Value[i].Note || other[i].Link.NoteLine != kv.Value[i].Link.NoteLine) return false;
            }
            return true;
        }

        private VisualElement CreateNoteMarker()
        {
            var marker = new Label
            {
                name = "noteMarker",
                style =
                {
                    width = NOTE_MARKER_WIDTH,
                    flexShrink = 0,
                    fontSize = 9,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    paddingLeft = 0, paddingRight = 0, marginLeft = 0, marginRight = 0
                }
            };
            marker.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0 || !(marker.userData is int line) || !m_NoteMarkers.TryGetValue(line, out var hits)) return;
                CodeNotes.OpenHits(hits, $"Notes at line {line + 1}");
                e.StopPropagation();
            });
            return marker;
        }

        private void BindNoteMarker(VisualElement row, int realIndex)
        {
            var marker = row.Q<Label>("noteMarker");
            if (marker == null) return;

            if (realIndex >= 0 && m_NoteMarkers.TryGetValue(realIndex, out var hits))
            {
                marker.userData = realIndex;
                marker.text = hits.Count > 1 ? "◆" : "●";
                marker.style.color = CodeIDESettings.Current.Theme.KeywordColor;
                marker.tooltip = BuildMarkerTooltip(hits);
                marker.pickingMode = PickingMode.Position;
            }
            else
            {
                marker.userData = null;
                marker.text = "";
                marker.tooltip = "";
                marker.pickingMode = PickingMode.Ignore;
            }
        }

        private static string BuildMarkerTooltip(List<NoteRefHit> hits)
        {
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var hit in hits)
            {
                if (shown++ == 5) { sb.Append($"\n… and {hits.Count - 5} more"); break; }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("📝 ").Append(hit.Note.Title);
                if (!string.IsNullOrEmpty(hit.Link.Context)) sb.Append(" — ").Append(hit.Link.Context);
            }
            sb.Append("\n(click to open)");
            return sb.ToString();
        }
    }
}
