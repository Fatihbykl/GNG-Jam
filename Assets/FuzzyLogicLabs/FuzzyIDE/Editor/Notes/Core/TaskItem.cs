using System;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    public enum TaskStatus
    {
        ToDo,
        InProgress,
        Done
    }

    [Serializable]
    public class TaskItem
    {
        public string NoteFilePath;
        public string NoteTitle;
        public int LineIndex;
        public string Text;
        public TaskStatus Status;
        
        // Extracted Unity Links if any
        public string CodeLink;      // e.g. "PlayerMotor.cs:42" or "Enemy#TakeDamage"
        public string AssetLink;     // e.g. "Assets/Prefabs/Enemy.prefab" or GUID
        public string RawLine;

        public static readonly Regex TaskRegex = new Regex(@"^\s*[-*]\s*\[([ xX/])\]\s*(.*)$", RegexOptions.Compiled);
        private static readonly Regex CodeLinkRegex = new Regex(@"\[\[([a-zA-Z0-9_\-\.\/\\]+\.(?:cs|shader|compute|asmdef)(?::\d+|#[a-zA-Z0-9_]+)?)\]\]", RegexOptions.Compiled);
        private static readonly Regex AssetLinkRegex = new Regex(@"\[\[asset:([^\]]+)\]\]", RegexOptions.Compiled);

        public static bool TryParse(string line, int lineIndex, string noteFilePath, string noteTitle, out TaskItem task)
        {
            task = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            var match = TaskRegex.Match(line);
            if (!match.Success) return false;

            char mark = match.Groups[1].Value[0];
            string text = match.Groups[2].Value.Trim();

            TaskStatus status = TaskStatus.ToDo;
            if (mark == 'x' || mark == 'X') status = TaskStatus.Done;
            else if (mark == '/') status = TaskStatus.InProgress;

            string codeLink = null;
            var codeMatch = CodeLinkRegex.Match(text);
            if (codeMatch.Success)
            {
                codeLink = codeMatch.Groups[1].Value;
            }

            string assetLink = null;
            var assetMatch = AssetLinkRegex.Match(text);
            if (assetMatch.Success)
            {
                assetLink = assetMatch.Groups[1].Value;
            }

            task = new TaskItem
            {
                NoteFilePath = noteFilePath,
                NoteTitle = noteTitle,
                LineIndex = lineIndex,
                Text = text,
                Status = status,
                CodeLink = codeLink,
                AssetLink = assetLink,
                RawLine = line
            };
            return true;
        }

        public string ToMarkdownLine(string indentation = "")
        {
            char mark = ' ';
            if (Status == TaskStatus.Done) mark = 'x';
            else if (Status == TaskStatus.InProgress) mark = '/';

            return $"{indentation}- [{mark}] {Text}";
        }
    }
}
