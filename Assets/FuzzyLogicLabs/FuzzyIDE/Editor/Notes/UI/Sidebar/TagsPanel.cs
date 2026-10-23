using System;
using System.Linq;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>Left-sidebar pane listing every tag with its note count; choosing one searches for it.</summary>
    public class TagsPanel : VisualElement
    {
        private readonly ScrollView m_List;
        private readonly Action<string> m_OnTagChosen;
        private bool m_Visible;
        private bool m_ByName;

        public TagsPanel(Action<string> onTagChosen)
        {
            m_OnTagChosen = onTagChosen;
            style.flexGrow = 1;

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingRight = 6, paddingTop = 2 } };
            header.Add(new Label("Tags") { style = { flexGrow = 1, color = NotesTheme.TextFaint } }.WithClass("fn-section-title"));
            Button sort = null;
            sort = NotesIcons.IconButton("arrow-up-down", "Sort by name / by count", () =>
            {
                m_ByName = !m_ByName;
                NotesIcons.SetToggled(sort, m_ByName);
                Refresh();
            }, 14f).WithClass("fn-icon-btn--small");
            header.Add(sort);
            Add(header);

            m_List = new ScrollView { style = { flexGrow = 1 } };
            Add(m_List);

            RegisterCallback<AttachToPanelEvent>(_ => NoteStore.OnNotesChanged += Refresh);
            RegisterCallback<DetachFromPanelEvent>(_ => NoteStore.OnNotesChanged -= Refresh);
        }

        public void SetVisible(bool visible)
        {
            m_Visible = visible;
            if (visible) Refresh();
        }

        private void Refresh()
        {
            if (!m_Visible) return;
            m_List.Clear();

            var tags = NoteStore.Instance.Index.TagCounts;
            if (tags.Count == 0)
            {
                m_List.Add(new Label("No tags yet. Add #tags to your notes or a 'tags' property.") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-empty"));
                return;
            }

            var ordered = m_ByName
                ? tags.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                : tags.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in ordered)
            {
                string tag = kv.Key;
                int depth = tag.Count(c => c == '/');
                var row = new VisualElement { style = { paddingLeft = 8 + depth * 14 } }.WithClass("fn-row");
                row.Add(NotesIcons.Create("hash", 14f, NotesTheme.TextFaint));
                row.Add(new Label(depth > 0 ? tag.Substring(tag.LastIndexOf('/') + 1) : tag) { tooltip = "#" + tag, style = { marginLeft = 6, color = NotesTheme.TextMuted } }.WithClass("fn-row-label"));
                row.Add(new Label(kv.Value.ToString()) { style = { color = NotesTheme.TextFaint } }.WithClass("fn-row-meta"));
                row.RegisterCallback<ClickEvent>(_ => m_OnTagChosen?.Invoke(tag));
                m_List.Add(row);
            }
        }
    }
}
