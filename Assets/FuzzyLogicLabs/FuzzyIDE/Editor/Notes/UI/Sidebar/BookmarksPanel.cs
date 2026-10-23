using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>Left-sidebar pane listing bookmarked (pinned) notes.</summary>
    public class BookmarksPanel : VisualElement
    {
        private readonly ScrollView m_List;
        private bool m_Visible;

        public BookmarksPanel()
        {
            style.flexGrow = 1;
            m_List = new ScrollView { style = { flexGrow = 1 } };
            Add(m_List);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged += Refresh;
                NoteStore.OnActiveNoteChanged += OnActiveNoteChanged;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged -= Refresh;
                NoteStore.OnActiveNoteChanged -= OnActiveNoteChanged;
            });
        }

        public void SetVisible(bool visible)
        {
            m_Visible = visible;
            if (visible) Refresh();
        }

        private void OnActiveNoteChanged(NoteItem note) => Refresh();

        private void Refresh()
        {
            if (!m_Visible) return;
            m_List.Clear();
            m_List.Add(new Label("Bookmarks") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-section-title"));

            var store = NoteStore.Instance;
            var notes = store.Notes.Where(n => n.IsPinned).OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ToList();
            if (notes.Count == 0)
            {
                m_List.Add(new Label("No bookmarks yet. Use the star in a note's header to add one.") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-empty"));
                return;
            }

            foreach (var note in notes)
            {
                var n = note;
                bool active = n == store.ActiveNote;
                var row = new VisualElement { style = { paddingLeft = 8, backgroundColor = active ? new StyleColor(NotesTheme.BgActive) : new StyleColor(StyleKeyword.Null) } }.WithClass("fn-row");
                row.Add(NotesIcons.Create("star", 15f, NotesTheme.Warning));
                row.Add(new Label(n.Title) { tooltip = n.RelativePath, style = { marginLeft = 6, color = active ? NotesTheme.TextNormal : NotesTheme.TextMuted } }.WithClass("fn-row-label"));
                if (n.Folder.Length > 0) row.Add(new Label(n.Folder) { style = { color = NotesTheme.TextFaint } }.WithClass("fn-row-meta"));
                row.RegisterCallback<ClickEvent>(e => FuzzyNotesWindow.OpenNote(n, -1, e.altKey ? NoteOpenMode.SplitRight : NoteOpenMode.Default));
                row.RegisterCallback<ContextClickEvent>(e =>
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Open"), false, () => FuzzyNotesWindow.OpenNote(n));
                    menu.AddItem(new GUIContent("Open to the Right"), false, () => FuzzyNotesWindow.OpenNote(n, -1, NoteOpenMode.SplitRight));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Remove Bookmark"), false, () => store.TogglePin(n));
                    menu.ShowAsContext();
                    e.StopPropagation();
                });
                m_List.Add(row);
            }
        }
    }
}
