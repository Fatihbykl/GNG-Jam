using System;
using System.IO;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>
    /// File details on the right of the status bar: git branch, line endings (click to convert), encoding,
    /// indentation and language; the caret label also shows the selection size.
    /// </summary>
    public partial class VirtualIDEWindow
    {
        private Label m_StatusGitLabel;
        private Label m_StatusEolLabel;
        private Label m_StatusEncodingLabel;
        private Label m_StatusIndentLabel;
        private Label m_StatusLanguageLabel;
        private string m_StatusFileKey;

        private Label MakeStatusItem(string tooltip, Action onClick)
        {
            var label = new Label { tooltip = tooltip };
            label.style.fontSize = 11;
            label.style.color = new Color(0.65f, 0.65f, 0.65f);
            label.style.marginRight = 12;
            label.style.paddingLeft = 3;
            label.style.paddingRight = 3;
            if (onClick != null)
            {
                label.RegisterCallback<ClickEvent>(e => onClick());
                label.RegisterCallback<MouseEnterEvent>(e => label.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f));
                label.RegisterCallback<MouseLeaveEvent>(e => label.style.backgroundColor = Color.clear);
            }
            return label;
        }

        /// <summary>Adds the file detail items in front of the caret label; called once the base status bar exists.</summary>
        private void ExtendStatusBar()
        {
            if (m_StatusBar == null || m_StatusCaretLabel == null) return;

            m_StatusDiagnosticsLabel.tooltip = "Click: Problems panel (Ctrl+Shift+M) | Right-click: export to Fuzzy Notes";
            m_StatusDiagnosticsLabel.RegisterCallback<ClickEvent>(e => ToggleProblems());

            m_StatusGitLabel = MakeStatusItem("Current git branch (click to open the Git window)", () => Git.GitWindow.ShowWindow());
            m_StatusEolLabel = MakeStatusItem("Line endings used when saving (click to change)", ShowLineEndingMenu);
            m_StatusEncodingLabel = MakeStatusItem("File encoding (preserved on save)", null);
            m_StatusIndentLabel = MakeStatusItem("Indentation (Settings > Editor > Tab Width)", () => SettingsService.OpenUserPreferences("Preferences/Fuzzy Logic Labs/Code IDE"));
            m_StatusLanguageLabel = MakeStatusItem("Language", null);

            int index = m_StatusBar.IndexOf(m_StatusCaretLabel);
            foreach (var item in new[] { m_StatusGitLabel, m_StatusIndentLabel, m_StatusEncodingLabel, m_StatusEolLabel, m_StatusLanguageLabel })
                m_StatusBar.Insert(index++, item);
            RefreshStatusFileInfo(force: true);
        }

        /// <summary>Re-reads file details when the active file changed (or when forced, e.g. after a save).</summary>
        private void RefreshStatusFileInfo(bool force = false)
        {
            if (m_StatusEolLabel == null) return;
            string branch = Git.GitService.RepositoryRoot != null ? Git.GitService.CurrentBranch : null;
            m_StatusGitLabel.text = string.IsNullOrEmpty(branch) ? "" : "\u2387 " + branch;
            m_StatusGitLabel.style.display = string.IsNullOrEmpty(branch) ? DisplayStyle.None : DisplayStyle.Flex;

            string path = m_ActivePane?.ActiveTab;
            string key = path + "|" + (path != null && m_OpenedFiles.TryGetValue(path, out var st) ? st.LineEndingOverride : "");
            if (!force && key == m_StatusFileKey) return;
            m_StatusFileKey = key;

            bool hasFile = !string.IsNullOrEmpty(path);
            foreach (var label in new[] { m_StatusEolLabel, m_StatusEncodingLabel, m_StatusIndentLabel, m_StatusLanguageLabel })
                label.style.display = hasFile ? DisplayStyle.Flex : DisplayStyle.None;
            if (!hasFile) return;

            string absolute = Path.GetFullPath(path);
            var format = TextFileFormat.DetectFile(absolute);
            string newLine = m_OpenedFiles.TryGetValue(path, out var state) && state.LineEndingOverride != null ? state.LineEndingOverride : format.NewLine;
            m_StatusEolLabel.text = newLine == "\r\n" ? "CRLF" : "LF";
            m_StatusEncodingLabel.text = format.HasBom ? "UTF-8 with BOM" : "UTF-8";
            m_StatusIndentLabel.text = "Spaces: " + CodeIDESettings.Current.Editor.TabWidth;
            m_StatusLanguageLabel.text = LanguageName(path);
        }

        private void ShowLineEndingMenu()
        {
            string path = m_ActivePane?.ActiveTab;
            if (string.IsNullOrEmpty(path) || !m_OpenedFiles.TryGetValue(path, out var state)) return;
            string current = m_StatusEolLabel.text == "CRLF" ? "\r\n" : "\n";
            var menu = new GenericMenu();
            foreach (var option in new[] { ("LF (Unix, macOS)", "\n"), ("CRLF (Windows)", "\r\n") })
            {
                string newLine = option.Item2;
                menu.AddItem(new GUIContent(option.Item1), current == newLine, () =>
                {
                    if (current == newLine) return;
                    // Applied on the next save; marking the file dirty makes that save happen.
                    state.LineEndingOverride = newLine;
                    state.IsDirty = true;
                    RefreshTabBar(m_ActivePane);
                    UpdateWindowTitle();
                    RefreshStatusFileInfo(force: true);
                });
            }
            menu.ShowAsContext();
        }

        private static string LanguageName(string path)
        {
            switch (Path.GetExtension(path)?.ToLowerInvariant())
            {
                case ".cs": return "C#";
                case ".shader": return "ShaderLab";
                case ".hlsl": case ".cginc": case ".compute": return "HLSL";
                case ".json": case ".asmdef": case ".asmref": return "JSON";
                case ".uss": return "USS";
                case ".uxml": return "UXML";
                case ".xml": return "XML";
                case ".md": return "Markdown";
                case ".yaml": case ".yml": return "YAML";
                default: return "Plain Text";
            }
        }

        private string SelectionSuffix()
        {
            var viewer = m_ActivePane?.Viewer;
            if (viewer == null) return "";
            int chars = viewer.SelectedCharCount;
            if (chars <= 0) return "";
            int lines = viewer.SelectedLineCount;
            return lines > 1 ? $" ({chars} selected, {lines} lines)" : $" ({chars} selected)";
        }
    }
}
