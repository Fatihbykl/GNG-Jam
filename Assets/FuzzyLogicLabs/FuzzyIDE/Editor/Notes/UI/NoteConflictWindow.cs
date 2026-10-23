using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>Side-by-side comparison of the disk version and the unsaved local version of a conflicted note.</summary>
    public class NoteConflictWindow : EditorWindow
    {
        private NoteItem m_Note;

        public static void Open(NoteItem note)
        {
            if (note == null || !note.HasConflict) return;
            var window = GetWindow<NoteConflictWindow>(true, $"Conflict: {note.Title}");
            window.minSize = new Vector2(760, 420);
            window.m_Note = note;
            window.Rebuild();
        }

        private void OnEnable()
        {
            NoteStore.OnNoteReloaded += OnNoteChanged;
            NoteStore.OnNoteSaved += OnNoteChanged;
            NoteStore.OnNoteConflict += OnNoteConflict;
        }

        private void OnDisable()
        {
            NoteStore.OnNoteReloaded -= OnNoteChanged;
            NoteStore.OnNoteSaved -= OnNoteChanged;
            NoteStore.OnNoteConflict -= OnNoteConflict;
        }

        private void OnNoteChanged(NoteItem note)
        {
            if (note == m_Note && !note.HasConflict) Close();
        }

        private void OnNoteConflict(NoteItem note)
        {
            if (note == m_Note) Rebuild();
        }

        private void CreateGUI() => Rebuild();

        private void Rebuild()
        {
            rootVisualElement.Clear();
            if (m_Note == null || !m_Note.HasConflict) return;

            NotesTheme.Apply(rootVisualElement);

            var info = new Label($"'{m_Note.Title}' was changed outside Fuzzy Notes while you had unsaved edits. Changed lines are highlighted.")
            {
                style = { whiteSpace = WhiteSpace.Normal, color = NotesTheme.TextNormal, marginLeft = 8, marginRight = 8, marginTop = 8, marginBottom = 6 }
            };
            rootVisualElement.Add(info);

            var diskLines = SplitLines(m_Note.ConflictDiskContent);
            var mineLines = SplitLines(m_Note.Serialize());

            var columns = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };
            columns.Add(BuildColumn("On Disk", diskLines, GitDiffEngine.ComputeDiff(mineLines, diskLines), NotesTheme.WithAlpha(NotesTheme.Error, 0.25f)));
            columns.Add(BuildColumn("Your Unsaved Version", mineLines, GitDiffEngine.ComputeDiff(diskLines, mineLines), NotesTheme.WithAlpha(NotesTheme.Success, 0.25f)));
            rootVisualElement.Add(columns);

            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, paddingTop = 6, paddingBottom = 8, paddingRight = 8 } };
            buttons.Add(new Button(() => NoteStore.Instance.ResolveConflictKeepBoth(m_Note))
            {
                text = "Keep Both",
                tooltip = "Save your version as a 'conflicted copy' note and load the disk version"
            });
            buttons.Add(new Button(() => NoteStore.Instance.ResolveConflictUseDisk(m_Note))
            {
                text = "Use Disk Version",
                tooltip = "Discard your unsaved edits"
            });
            buttons.Add(new Button(() => NoteStore.Instance.ResolveConflictKeepMine(m_Note))
            {
                text = "Keep Mine",
                tooltip = "Overwrite the file on disk with your version"
            });
            rootVisualElement.Add(buttons);
        }

        private static VisualElement BuildColumn(string title, List<string> lines, GitLineDiffResult diff, Color highlight)
        {
            var column = new VisualElement { style = { flexGrow = 1, flexBasis = 0, marginLeft = 6, marginRight = 6 } };
            column.Add(new Label(title) { style = { unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextNormal, marginBottom = 4 } });

            var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal)
            {
                style = { flexGrow = 1, borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1, borderTopColor = NotesTheme.Border, borderBottomColor = NotesTheme.Border, borderLeftColor = NotesTheme.Border, borderRightColor = NotesTheme.Border }
            };
            for (int i = 0; i < lines.Count; i++)
            {
                bool changed = diff.LineStatuses.TryGetValue(i, out var status) && status != GitLineDiffType.None;
                var row = new Label(lines[i].Length == 0 ? " " : lines[i])
                {
                    enableRichText = false,
                    style =
                    {
                        color = NotesTheme.TextNormal,
                        backgroundColor = changed ? highlight : Color.clear,
                        paddingLeft = 4,
                        whiteSpace = WhiteSpace.NoWrap
                    }
                };
                scroll.Add(row);
            }
            column.Add(scroll);
            return column;
        }

        private static List<string> SplitLines(string text) =>
            new List<string>(MarkdownScan.NormalizeNewlines(text).Split('\n'));
    }
}
