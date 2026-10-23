using System.Collections.Generic;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    public static class CodeToNoteCommand
    {
        /// <summary>"Add to Fuzzy Notes…" from the code editor: asks for the target note, then appends a symbol-anchored link and the code.</summary>
        /// <param name="line">1-based first line of the selection.</param>
        /// <param name="lines">Current editor buffer (for the enclosing symbol); may be null.</param>
        public static void AddCodeSnippetToNotes(string assetPath, int line, string codeSnippet, IList<string> lines = null)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            CodeNotes.AddCodeToNote(assetPath, System.Math.Max(0, line - 1), lines, codeSnippet);
        }
    }
}
