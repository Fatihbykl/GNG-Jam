using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;
using FuzzyLogicLabs.FuzzyIDE.Editor.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>
    /// .md files of the Unity project: opening them in Fuzzy Notes (double-click) or as source in Fuzzy IDE, and
    /// keeping the Assets vault in sync with imports, deletes and moves.
    /// </summary>
    public static class MarkdownAssets
    {
        public static bool IsMarkdown(string path) => !string.IsNullOrEmpty(path) && path.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Opens a .md file (asset path or absolute) in Fuzzy Notes: as a vault note when a vault lists it.</summary>
        public static bool OpenInNotes(string path, int line = -1)
        {
            if (!IsMarkdown(path)) return false;
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) return false;

            // The window loads the vaults when it opens; look the note up afterwards so it is the loaded instance.
            FuzzyNotesWindow.ShowWindow();
            var store = NoteStore.Instance;
            var note = store.FindByPath(full) ?? store.OpenExternalFile(full);
            if (note == null) return false;
            FuzzyNotesWindow.OpenNote(note, BodyLine(note, line));
            return true;
        }

        /// <summary>Opens the note's file as plain text in Fuzzy IDE (unsaved edits are saved first).</summary>
        public static void OpenSourceInIde(NoteItem note)
        {
            if (note == null || string.IsNullOrEmpty(note.FilePath)) return;
            if (note.IsDirty) NoteStore.Instance.SaveNote(note, immediate: true);
            var window = EditorWindow.GetWindow<VirtualIDEWindow>();
            window.Show();
            window.LoadFileAndJumpToLine(note.FilePath, 1);
            window.Focus();
        }

        /// <summary>Unity line numbers count the whole file; the note editor scrolls to body lines.</summary>
        private static int BodyLine(NoteItem note, int fileLine)
        {
            if (fileLine <= 0) return -1;
            int frontMatterLines = (note.RawContent ?? "").Split('\n').Length - (note.Body ?? "").Split('\n').Length;
            return System.Math.Max(0, fileLine - 1 - frontMatterLines);
        }

        private sealed class Postprocessor : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (!NoteStore.HasInstance) return;
                var paths = new List<string>();
                foreach (var list in new[] { imported, deleted, moved, movedFrom })
                    paths.AddRange(list.Where(IsMarkdown));
                if (paths.Count == 0) return;

                // Outside the import callback: the store raises UI events and may read the files.
                EditorApplication.delayCall += () => NoteStore.Instance.NotifyProjectFilesChanged(paths);
            }
        }
    }
}
