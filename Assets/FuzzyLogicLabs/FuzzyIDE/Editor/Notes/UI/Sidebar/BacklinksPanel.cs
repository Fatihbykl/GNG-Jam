using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Right-sidebar pane: notes that link to the active note (with the linking lines as context) and notes that
    /// mention its title without linking. Work is only done while the pane is visible.
    /// </summary>
    public class BacklinksPanel : VisualElement
    {
        private const int MaxContextLines = 3;

        private readonly ScrollView m_List;
        private NoteItem m_Note;
        private bool m_Visible;
        private IVisualElementScheduledItem m_Pending;

        public BacklinksPanel()
        {
            style.flexGrow = 1;
            m_List = new ScrollView { style = { flexGrow = 1 } };
            Add(m_List);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged += RequestRefresh;
                NoteStore.OnNoteSaved += OnNoteEvent;
                NoteStore.OnNoteReloaded += OnNoteEvent;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged -= RequestRefresh;
                NoteStore.OnNoteSaved -= OnNoteEvent;
                NoteStore.OnNoteReloaded -= OnNoteEvent;
            });
        }

        public void Bind(NoteItem note)
        {
            if (note == m_Note) return;
            m_Note = note;
            if (m_Visible) Rebuild();
        }

        public void SetVisible(bool visible)
        {
            m_Visible = visible;
            if (visible) Rebuild();
        }

        private void OnNoteEvent(NoteItem note) => RequestRefresh();

        private void RequestRefresh()
        {
            if (!m_Visible) return;
            m_Pending?.Pause();
            m_Pending = schedule.Execute(Rebuild).StartingIn(250);
        }

        private void Rebuild()
        {
            m_List.Clear();
            if (m_Note == null)
            {
                m_List.Add(Empty("Open a note to see what links to it."));
                return;
            }

            var store = NoteStore.Instance;
            var target = m_Note;
            var paths = store.Index.GetBacklinkPaths(target);

            m_List.Add(SectionTitle("Linked mentions", paths.Count));
            if (paths.Count == 0) m_List.Add(Empty("No other notes link to this note."));
            foreach (var path in paths)
            {
                var source = store.FindByPath(path);
                if (source == null) continue;
                m_List.Add(SourceRow(source, null, null));
                foreach (var (line, text) in LinkingLines(source, target))
                    m_List.Add(ContextRow(source, line, text));
            }

            var mentions = store.FindUnlinkedMentions(target);
            m_List.Add(SectionTitle("Unlinked mentions", mentions.Count));
            if (mentions.Count == 0) m_List.Add(Empty("No plain-text mentions of this note's title."));
            foreach (var (source, count) in mentions)
            {
                var link = new Button(() => store.LinkMentions(source, target))
                {
                    text = "Link",
                    tooltip = $"Turn the plain mentions in '{source.Title}' into [[{target.LinkName}]] links",
                    style = { height = 20, color = NotesTheme.TextNormal }
                }.WithClass("fn-btn");
                link.RegisterCallback<ClickEvent>(e => e.StopPropagation());
                m_List.Add(SourceRow(source, count.ToString(), link));
            }
        }

        /// <summary>Lines of <paramref name="source"/> that contain a wiki link to <paramref name="target"/>.</summary>
        private static List<(int line, string text)> LinkingLines(NoteItem source, NoteItem target)
        {
            var result = new List<(int, string)>();
            var names = new List<string> { target.LinkName, target.Title };
            names.AddRange(target.Aliases);

            var lines = MarkdownScan.NormalizeNewlines(source.Body ?? "").Split('\n');
            for (int i = 0; i < lines.Length && result.Count < MaxContextLines; i++)
            {
                string line = lines[i];
                if (line.IndexOf("[[", StringComparison.Ordinal) < 0) continue;
                foreach (var name in names)
                {
                    if (string.IsNullOrEmpty(name) || line.IndexOf("[[" + name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    result.Add((i, line.Trim()));
                    break;
                }
            }
            return result;
        }

        private static Label SectionTitle(string text, int count) =>
            new Label($"{text} · {count}") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-section-title");

        private static Label Empty(string text) => new Label(text) { style = { color = NotesTheme.TextFaint } }.WithClass("fn-empty");

        private static VisualElement SourceRow(NoteItem source, string meta, VisualElement trailing)
        {
            var row = new VisualElement { style = { paddingLeft = 8 } }.WithClass("fn-row");
            row.Add(NotesIcons.Create("file-text", 15f, NotesTheme.TextFaint));
            row.Add(new Label(string.IsNullOrEmpty(source.Title) ? Path.GetFileNameWithoutExtension(source.FilePath) : source.Title)
            {
                tooltip = source.RelativePath,
                style = { marginLeft = 6, color = NotesTheme.TextNormal }
            }.WithClass("fn-row-label"));
            if (meta != null) row.Add(new Label(meta) { style = { color = NotesTheme.TextFaint, marginRight = 6 } }.WithClass("fn-row-meta"));
            if (trailing != null) row.Add(trailing);
            row.RegisterCallback<ClickEvent>(_ => FuzzyNotesWindow.OpenNote(source));
            return row;
        }

        private static VisualElement ContextRow(NoteItem source, int line, string text)
        {
            var row = new Label(text)
            {
                enableRichText = false,
                tooltip = "Open at this line",
                style =
                {
                    height = StyleKeyword.Auto, minHeight = 22,
                    marginLeft = 28, paddingLeft = 8, paddingTop = 3, paddingBottom = 3,
                    whiteSpace = WhiteSpace.Normal, fontSize = 12, color = NotesTheme.TextMuted,
                    borderLeftWidth = 2, borderLeftColor = NotesTheme.BorderSubtle
                }
            }.WithClass("fn-row");
            row.RegisterCallback<ClickEvent>(_ => FuzzyNotesWindow.OpenNote(source, line));
            return row;
        }
    }
}
