using System;
using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>
    /// "Which note should this go to?" — used by every "add to note" action in the IDE, Inspector and menus.
    /// Offers the last used note, the active note, today's daily note, a suggested note and fuzzy search over all notes.
    /// </summary>
    public static class NoteTargetPicker
    {
        private static string s_LastTargetPath;

        /// <param name="placeholder">Picker prompt, e.g. "Add code to note…".</param>
        /// <param name="suggestedTitle">Note offered first, created if missing (e.g. "Notes on PlayerMotor.cs"). Optional.</param>
        /// <param name="onPicked">Receives the chosen (possibly newly created) note.</param>
        public static void Pick(string placeholder, string suggestedTitle, Action<NoteItem> onPicked)
        {
            var store = NoteStore.Instance;
            store.EnsureLoaded();

            void Choose(NoteItem note)
            {
                if (note == null) return;
                s_LastTargetPath = note.FilePath;
                onPicked(note);
            }

            NotePickerWindow.Open(placeholder, "Enter choose · Shift+Enter new note named as typed · Esc cancel",
                query => BuildItems(query, suggestedTitle, Choose),
                (query, forced) => Choose(store.CreateNote(query.Trim(), $"# {query.Trim()}\n")));
        }

        private static List<PaletteItem> BuildItems(string query, string suggestedTitle, Action<NoteItem> choose)
        {
            var store = NoteStore.Instance;
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            var items = new List<PaletteItem>();
            var listed = new HashSet<NoteItem>();

            void Add(PaletteItem item)
            {
                if (hasQuery)
                {
                    var matches = new List<int>();
                    item.Score = FuzzyMatcher.Score(query, item.Title, matches);
                    if (item.Score == FuzzyMatcher.NoMatch) return;
                    item.Matches = matches;
                }
                items.Add(item);
            }

            void AddNote(NoteItem note, string icon, string hint, int bonus)
            {
                if (note == null || !listed.Add(note)) return;
                var n = note;
                int before = items.Count;
                Add(new PaletteItem { Title = note.Title, Subtitle = note.RelativePath, Icon = icon, Hint = hint, OnChoose = () => choose(n) });
                if (items.Count > before) items[items.Count - 1].Score += bonus;
            }

            // 1. Shortcuts
            AddNote(string.IsNullOrEmpty(s_LastTargetPath) ? null : store.FindByPath(s_LastTargetPath), "undo-2", "last used", 40);
            AddNote(store.ActiveNote, "pencil", "open note", 30);

            if (!string.IsNullOrWhiteSpace(suggestedTitle))
            {
                var existing = store.Notes.FirstOrDefault(n => string.Equals(n.Title, suggestedTitle, StringComparison.OrdinalIgnoreCase));
                if (existing != null) AddNote(existing, "star", "suggested", 25);
                else
                {
                    int before = items.Count;
                    Add(new PaletteItem
                    {
                        Title = suggestedTitle,
                        Subtitle = "New note",
                        Icon = "file-plus",
                        Hint = "new",
                        OnChoose = () => choose(store.CreateNote(suggestedTitle, $"# {suggestedTitle}\n"))
                    });
                    if (items.Count > before) items[items.Count - 1].Score += 20;
                }
            }

            {
                int before = items.Count;
                Add(new PaletteItem
                {
                    Title = "Today's daily note",
                    Subtitle = NoteStore.DailyNoteName(DateTime.Today, NoteSettings.DailyOptions),
                    Icon = "calendar",
                    OnChoose = () => choose(store.OpenDailyNote(DateTime.Today, NoteSettings.DailyOptions, NoteTemplateContextFactory.Create()))
                });
                if (items.Count > before) items[items.Count - 1].Score += 15;
            }

            // 2. Every other note: recent first, then most recently updated
            var recent = store.RecentPaths;
            foreach (var path in recent) AddNote(store.FindByPath(path), "file-text", null, 10);
            foreach (var note in store.Notes.OrderByDescending(n => n.UpdatedAt)) AddNote(note, "file-text", null, 0);

            if (hasQuery) items = items.OrderByDescending(i => i.Score).ToList();
            return items;
        }
    }
}
