using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    public static class GitNoteBridge
    {
        public static void StampActiveNoteWithGitInfo(NoteItem note)
        {
            if (note == null) return;

            string branch = GitService.IsGitRepository ? GitService.CurrentBranch : "main";
            string stamp = $"\n\n> 🌿 **Git Branch:** `{branch}` | 🕒 **Timestamp:** `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC`\n";

            NoteStore.Instance.AppendToNote(note, stamp);
        }

        /// <summary>Snapshot of every code TODO comment as a task list note (the live list is in Ctrl+P → "Go to code TODO").</summary>
        public static void ScanAndCollectProjectTodos()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/') + "/";
            var csFiles = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories);
            var sb = new StringBuilder();
            sb.AppendLine("# Codebase TODO Inbox 📥");
            sb.AppendLine($"*Scanned on {DateTime.Now:yyyy-MM-dd HH:mm} across {csFiles.Length} scripts*\n");

            int totalFound = 0;
            foreach (var file in csFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string assetPath = file.Replace('\\', '/');
                if (assetPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)) assetPath = assetPath.Substring(projectRoot.Length);

                System.Collections.Generic.List<CodeTodo> todos;
                try { todos = CodeTodoScanner.Scan(assetPath, File.ReadAllText(file)); }
                catch (IOException) { continue; }
                if (todos.Count == 0) continue;

                sb.AppendLine($"\n### 📄 [[{Path.GetFileName(file)}]]");
                foreach (var todo in todos)
                {
                    sb.AppendLine($"- [ ] **{todo.Kind}** [[{todo.Link}]]: {todo.Text}");
                    totalFound++;
                }
            }

            if (totalFound == 0)
            {
                sb.AppendLine("\n🎉 No TODO/FIXME comments found in project scripts!");
            }

            var note = NoteStore.Instance.CreateNote("Codebase TODO Inbox", sb.ToString(), new System.Collections.Generic.List<string> { "todo", "code" });
            FuzzyNotesWindow.OpenNote(note);
            EditorWindow.focusedWindow?.ShowNotification(new GUIContent($"Collected {totalFound} TODO comments"));
        }
    }
}
