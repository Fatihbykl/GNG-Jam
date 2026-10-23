using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>
    /// Sends compiler diagnostics to the project's "Bug Tracker" note as tasks. A diagnostic that is already
    /// listed as an open task is not added again.
    /// </summary>
    public static class DiagnosticsNoteBridge
    {
        public const string BugTrackerTitle = "Bug Tracker";

        public static void CreateBugNoteFromDiagnostic(string assetPath, string message, int line, bool isError)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            int added = AddDiagnostics(assetPath, new[] { (message, line, isError) }, null, out var tracker, out int revealLine);
            if (tracker != null) FuzzyNotesWindow.OpenNote(tracker, revealLine);
        }

        /// <summary>Adds diagnostics (1-based lines) as tasks to the Bug Tracker note, skipping open duplicates.</summary>
        /// <param name="lines">Editor buffer used to anchor tasks to symbols; may be null.</param>
        /// <returns>Number of new tasks.</returns>
        public static int AddDiagnostics(string assetPath, IEnumerable<(string message, int line, bool isError)> diagnostics,
                                         IList<string> lines, out NoteItem tracker, out int revealLine)
        {
            var store = NoteStore.Instance;
            store.EnsureLoaded();
            tracker = GetOrCreateTracker();
            revealLine = -1;

            string fileName = Path.GetFileName(CodeNotes.ToAssetPath(assetPath));
            var body = MarkdownScan.NormalizeNewlines(tracker.Body).Split('\n');
            var additions = new List<string>();

            foreach (var (message, line, isError) in diagnostics)
            {
                string text = (message ?? "").Replace('\n', ' ').Trim();
                if (text.Length == 0) continue;

                // Duplicate = an open task with the same message for the same file.
                int existing = Array.FindIndex(body, l => l.StartsWith("- [ ]") && l.Contains(text) && l.Contains("[[" + fileName));
                if (existing >= 0 || additions.Any(a => a.Contains(text)))
                {
                    if (revealLine < 0) revealLine = existing;
                    continue;
                }

                string link = lines != null ? CodeNotes.BuildCodeLink(assetPath, Math.Max(0, line - 1), lines) : $"{fileName}:{Math.Max(1, line)}";
                string icon = isError ? "🔴" : "🟡";
                additions.Add($"- [ ] {icon} {text} — [[{link}]] (line {Math.Max(1, line)}) #bug");
            }

            if (additions.Count > 0)
            {
                string block = $"\n### {fileName} · {DateTime.Now:yyyy-MM-dd HH:mm}\n" + string.Join("\n", additions) + "\n";
                store.AppendToNote(tracker, block);
                var updated = MarkdownScan.NormalizeNewlines(tracker.Body).Split('\n');
                revealLine = Array.FindLastIndex(updated, l => l.Contains(additions[0]));
            }
            return additions.Count;
        }

        private static NoteItem GetOrCreateTracker()
        {
            var store = NoteStore.Instance;
            var tracker = store.Notes.FirstOrDefault(n => n.VaultId == store.PrimaryVault.Id &&
                                                          string.Equals(n.Title, BugTrackerTitle, StringComparison.OrdinalIgnoreCase));
            return tracker ?? store.CreateNote(BugTrackerTitle, "# Bug Tracker\n\nCompiler diagnostics sent from Fuzzy IDE. Tick a task when it is fixed.\n",
                                               new List<string> { "bug" });
        }
    }
}
