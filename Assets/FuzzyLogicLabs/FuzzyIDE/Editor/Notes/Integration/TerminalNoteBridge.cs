using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Terminal;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    public static class TerminalNoteBridge
    {
        public static void RunCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            string trimmed = command.Trim();
            // Remove backticks or multiple lines if block
            if (trimmed.StartsWith("```"))
            {
                var lines = trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 2)
                {
                    var sb = new StringBuilder();
                    for (int i = 1; i < lines.Length - 1; i++)
                    {
                        sb.AppendLine(lines[i]);
                    }
                    trimmed = sb.ToString().Trim();
                }
            }

            try
            {
                // Find or open NativeTerminalWindow
                var termWin = EditorWindow.GetWindow<NativeTerminalWindow>();
                if (termWin != null)
                {
                    termWin.Show();
                    termWin.Focus();

                    // Call native terminal execution
                    NativeTerminalWindow.ExecuteCommand(trimmed);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] Could not send command to NativeTerminalWindow: {ex.Message}");
            }
        }
    }
}
