using System.Linq;
using UnityEditor;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>User-facing note operations that need confirmation dialogs, shared by the list and the editor.</summary>
    public static class NoteActions
    {
        /// <summary>Renames the note, asking whether incoming [[links]] should follow. Returns false if cancelled or failed.</summary>
        public static bool Rename(NoteItem note, string newTitle)
        {
            if (note == null || string.IsNullOrWhiteSpace(newTitle)) return false;
            newTitle = newTitle.Trim();
            if (newTitle == note.Title) return true;

            var store = NoteStore.Instance;
            bool updateLinks = false;
            int linkCount = store.CountIncomingLinks(note, out int noteCount);
            if (linkCount > 0)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Rename Note",
                    $"'{note.Title}' is linked {linkCount} time(s) from {noteCount} note(s).\n\nUpdate those links to point to '{newTitle}'?",
                    "Update Links", "Cancel", "Don't Update");
                if (choice == 1) return false;
                updateLinks = choice == 0;
            }

            if (store.RenameNote(note, newTitle, updateLinks)) return true;

            NoteToast.Show(note.HasConflict
                    ? "This note has a pending conflict with the file on disk. Resolve it before renaming."
                    : $"Could not rename to '{newTitle}'. A note with that name may already exist.",
                NoteToastKind.Error);
            return false;
        }

        /// <summary>Moves a note to the trash (an external note is only closed) and offers to undo it, like Obsidian.</summary>
        public static void Delete(NoteItem note)
        {
            if (note == null) return;
            DeleteMany(new[] { note });
        }

        /// <summary>Moves notes to the trash (external ones are closed, their files stay) with one "Undo" toast.</summary>
        public static void DeleteMany(System.Collections.Generic.IReadOnlyList<NoteItem> notes)
        {
            if (notes == null || notes.Count == 0) return;
            var store = NoteStore.Instance;

            // Files of the Assets vault are project assets and go to the system trash, out of reach of "Undo": always ask.
            int projectFiles = notes.Count(n => !n.IsExternal && store.VaultOf(n)?.IsProjectFiles == true);
            if (projectFiles > 0)
            {
                string question = notes.Count == 1
                    ? $"Move '{notes[0].Title}' from the project's Assets folder to the system trash?"
                    : $"Move {notes.Count} files to the trash?\n\n{projectFiles} of them are in the project's Assets folder and go to the system trash.";
                if (!EditorUtility.DisplayDialog("Delete", question, "Move to Trash", "Cancel")) return;
            }
            else if (NoteSettings.ConfirmDelete && !EditorUtility.DisplayDialog("Delete",
                    notes.Count == 1 ? $"Move '{notes[0].Title}' to the trash?" : $"Move {notes.Count} notes to the trash?\n\nExternal files are only closed, not deleted.",
                    "Move to Trash", "Cancel"))
                return;

            int trashed = 0, closed = 0, systemTrashed = 0;
            string firstTitle = notes[0].Title;
            foreach (var note in notes)
            {
                bool external = note.IsExternal;
                bool projectFile = !external && store.VaultOf(note)?.IsProjectFiles == true;
                if (!store.DeleteNote(note)) continue;
                if (external) closed++;
                else if (projectFile) systemTrashed++;
                else trashed++;
            }

            if (trashed == 0)
            {
                if (systemTrashed > 0) NoteToast.Show(systemTrashed == 1 ? $"Moved '{firstTitle}' to the system trash" : $"Moved {systemTrashed} files to the system trash");
                else if (closed > 0) NoteToast.Show(closed == 1 ? $"Closed '{firstTitle}' (the file was not deleted)" : $"Closed {closed} external notes");
                return;
            }

            string message = trashed == 1 && closed == 0 ? $"Moved '{firstTitle}' to the trash" : $"Moved {trashed} notes to the trash";
            NoteToast.Show(message, NoteToastKind.Info, "Undo", () =>
            {
                for (int i = 0; i < trashed; i++) store.RestoreLastDeleted();
            }, 8000);
        }
    }
}
