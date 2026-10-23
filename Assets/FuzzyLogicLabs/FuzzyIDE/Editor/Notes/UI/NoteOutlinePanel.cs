using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Heading outline of the note in the active pane; click to jump, highlights the section under the caret.
    /// Lives in the right sidebar and follows whichever editor <see cref="Bind"/> was last given.
    /// </summary>
    public class NoteOutlinePanel : VisualElement
    {
        private MarkdownLiveEditor m_Editor;
        private readonly ScrollView m_List;
        private readonly List<Label> m_Rows = new List<Label>();
        private List<MarkdownLiveEditor.HeadingInfo> m_Headings = new List<MarkdownLiveEditor.HeadingInfo>();
        private IVisualElementScheduledItem m_Pending;
        private int m_CurrentIndex = -1;

        public NoteOutlinePanel()
        {
            style.flexGrow = 1;

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingRight = 6, paddingTop = 2 } };
            header.Add(new Label("Outline") { style = { flexGrow = 1, color = NotesTheme.TextFaint } }.WithClass("fn-section-title"));
            header.Add(NotesIcons.IconButton("chevrons-down-up", "Fold all sections", () => m_Editor?.SetAllFolded(true), 14f).WithClass("fn-icon-btn--small"));
            header.Add(NotesIcons.IconButton("arrow-up-down", "Unfold all sections", () => m_Editor?.SetAllFolded(false), 14f).WithClass("fn-icon-btn--small"));
            Add(header);

            m_List = new ScrollView { style = { flexGrow = 1 } };
            Add(m_List);

            RegisterCallback<DetachFromPanelEvent>(_ => Bind(null));
        }

        /// <summary>Follows another editor (null: show nothing). Rebuilds immediately.</summary>
        public void Bind(MarkdownLiveEditor editor)
        {
            if (m_Editor != null)
            {
                m_Editor.OnTextChanged -= RequestRefresh;
                m_Editor.OnCaretMoved -= UpdateCurrent;
            }
            m_Editor = editor;
            if (m_Editor != null)
            {
                m_Editor.OnTextChanged += RequestRefresh;
                m_Editor.OnCaretMoved += UpdateCurrent;
            }
            Rebuild();
        }

        public void RequestRefresh()
        {
            m_Pending?.Pause();
            m_Pending = schedule.Execute(Rebuild).StartingIn(300);
        }

        public void Rebuild()
        {
            m_Pending?.Pause();
            m_Headings = m_Editor != null ? m_Editor.GetHeadings() : new List<MarkdownLiveEditor.HeadingInfo>();
            m_List.Clear();
            m_Rows.Clear();
            m_CurrentIndex = -1;

            if (m_Headings.Count == 0)
            {
                m_List.Add(new Label(m_Editor == null ? "Open a note to see its outline." : "No headings in this note.") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-empty"));
                return;
            }

            int minLevel = 6;
            foreach (var h in m_Headings) minLevel = Mathf.Min(minLevel, h.Level);

            foreach (var h in m_Headings)
            {
                int line = h.Line;
                var row = new Label(string.IsNullOrEmpty(h.Text) ? "(empty heading)" : h.Text)
                {
                    tooltip = h.Text,
                    style =
                    {
                        paddingLeft = 8 + (h.Level - minLevel) * 14,
                        height = 24,
                        unityTextAlign = TextAnchor.MiddleLeft,
                        color = NotesTheme.TextMuted
                    }
                }.WithClass("fn-row", "fn-row-label");
                row.RegisterCallback<ClickEvent>(_ => { m_Editor?.RevealLine(line); m_Editor?.FocusEditor(); });
                m_Rows.Add(row);
                m_List.Add(row);
            }
            UpdateCurrent();
        }

        /// <summary>Highlights the heading whose section contains the caret.</summary>
        public void UpdateCurrent()
        {
            if (m_Editor == null) return;
            int caretLine = m_Editor.Caret.Line;
            int index = -1;
            for (int i = 0; i < m_Headings.Count; i++)
            {
                if (m_Headings[i].Line <= caretLine) index = i;
                else break;
            }
            if (index == m_CurrentIndex || m_Rows.Count != m_Headings.Count) return;

            if (m_CurrentIndex >= 0 && m_CurrentIndex < m_Rows.Count)
            {
                m_Rows[m_CurrentIndex].style.backgroundColor = StyleKeyword.Null;
                m_Rows[m_CurrentIndex].style.color = NotesTheme.TextMuted;
            }
            m_CurrentIndex = index;
            if (index >= 0)
            {
                m_Rows[index].style.backgroundColor = NotesTheme.BgActive;
                m_Rows[index].style.color = NotesTheme.TextNormal;
                m_List.ScrollTo(m_Rows[index]);
            }
        }
    }
}
