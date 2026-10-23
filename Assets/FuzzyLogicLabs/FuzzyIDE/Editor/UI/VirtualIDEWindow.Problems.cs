using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>
    /// Problems panel: compiler and live Roslyn diagnostics of the whole project in one list, grouped by file,
    /// filterable by severity, text and current file. Ctrl+Shift+M or a click on the status bar counts toggles it.
    /// </summary>
    public partial class VirtualIDEWindow
    {
        private sealed class ProblemRow
        {
            public string Path;
            public bool IsHeader;
            public FileDiagnosticItem Item;
            public int Errors, Warnings;
        }

        private VisualElement m_ProblemsPanel;
        private Label m_ProblemsTitle;
        private Toggle m_ProblemsErrors, m_ProblemsWarnings, m_ProblemsCurrentFile;
        private TextField m_ProblemsFilter;
        private ListView m_ProblemsList;
        private readonly List<ProblemRow> m_ProblemRows = new List<ProblemRow>();
        private readonly HashSet<string> m_CollapsedProblemFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void InitializeProblemsUI()
        {
            var theme = CodeIDESettings.Current.Theme;
            m_ProblemsPanel = new VisualElement { name = "problems-panel" };
            m_ProblemsPanel.style.height = 220;
            m_ProblemsPanel.style.flexShrink = 0;
            m_ProblemsPanel.style.backgroundColor = theme.Background;
            m_ProblemsPanel.style.borderTopWidth = 2;
            m_ProblemsPanel.style.borderTopColor = theme.UIBorder;
            m_ProblemsPanel.style.display = DisplayStyle.None;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.backgroundColor = theme.UIPopupBackground;
            header.style.paddingLeft = 8; header.style.paddingRight = 4;
            header.style.minHeight = 24;

            m_ProblemsTitle = new Label("Problems");
            m_ProblemsTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_ProblemsTitle.style.marginRight = 12;
            header.Add(m_ProblemsTitle);

            m_ProblemsErrors = MakeProblemsToggle("Errors", true);
            m_ProblemsWarnings = MakeProblemsToggle("Warnings", true);
            m_ProblemsCurrentFile = MakeProblemsToggle("Current File", false);
            header.Add(m_ProblemsErrors);
            header.Add(m_ProblemsWarnings);
            header.Add(m_ProblemsCurrentFile);

            m_ProblemsFilter = new TextField { tooltip = "Filter by message or file name" };
            m_ProblemsFilter.style.flexGrow = 1;
            m_ProblemsFilter.style.marginLeft = 8;
            m_ProblemsFilter.RegisterValueChangedCallback(e => RefreshProblems());
            header.Add(m_ProblemsFilter);

            var close = new Button(ToggleProblems) { text = "X", tooltip = "Close (Ctrl+Shift+M)" };
            close.style.backgroundColor = Color.clear;
            close.style.borderTopWidth = 0; close.style.borderBottomWidth = 0; close.style.borderLeftWidth = 0; close.style.borderRightWidth = 0;
            header.Add(close);
            m_ProblemsPanel.Add(header);

            m_ProblemsList = new ListView { fixedItemHeight = 20, selectionType = SelectionType.Single };
            m_ProblemsList.style.flexGrow = 1;
            m_ProblemsList.itemsSource = m_ProblemRows;
            m_ProblemsList.makeItem = MakeProblemRow;
            m_ProblemsList.bindItem = BindProblemRow;
            m_ProblemsList.itemsChosen += items =>
            {
                if (items.FirstOrDefault() is ProblemRow row) OpenProblem(row);
            };
            m_ProblemsPanel.Add(m_ProblemsList);

            // Above the status bar, like the other bottom panels belong.
            int statusIndex = m_StatusBar != null ? rootVisualElement.IndexOf(m_StatusBar) : -1;
            if (statusIndex >= 0) rootVisualElement.Insert(statusIndex, m_ProblemsPanel);
            else rootVisualElement.Add(m_ProblemsPanel);
        }

        private Toggle MakeProblemsToggle(string label, bool value)
        {
            var toggle = new Toggle(label) { value = value };
            toggle.style.marginRight = 8;
            toggle.labelElement.style.minWidth = 0;
            toggle.labelElement.style.marginRight = 2;
            toggle.RegisterValueChangedCallback(e => RefreshProblems());
            return toggle;
        }

        private VisualElement MakeProblemRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            var icon = new Label { name = "icon" };
            icon.style.width = 18;
            icon.style.unityTextAlign = TextAnchor.MiddleCenter;
            var text = new Label { name = "text" };
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            text.style.overflow = Overflow.Hidden;
            text.style.textOverflow = TextOverflow.Ellipsis;
            var detail = new Label { name = "detail" };
            detail.style.color = new Color(0.55f, 0.55f, 0.55f);
            detail.style.marginLeft = 8;
            detail.style.marginRight = 8;
            row.Add(icon);
            row.Add(text);
            row.Add(detail);
            row.RegisterCallback<ClickEvent>(e =>
            {
                // Single click on a file header folds it; items open on double click / Enter (itemsChosen).
                if (row.userData is ProblemRow header && header.IsHeader)
                {
                    if (!m_CollapsedProblemFiles.Remove(header.Path)) m_CollapsedProblemFiles.Add(header.Path);
                    RefreshProblems();
                }
                else if (e.clickCount == 1 && row.userData is ProblemRow item)
                {
                    OpenProblem(item);
                }
            });
            return row;
        }

        private void BindProblemRow(VisualElement element, int index)
        {
            if (index < 0 || index >= m_ProblemRows.Count) return;
            var row = m_ProblemRows[index];
            element.userData = row;
            var icon = element.Q<Label>("icon");
            var text = element.Q<Label>("text");
            var detail = element.Q<Label>("detail");
            var theme = CodeIDESettings.Current.Theme;

            if (row.IsHeader)
            {
                icon.text = m_CollapsedProblemFiles.Contains(row.Path) ? "\u25B6" : "\u25BC";
                icon.style.color = new Color(0.7f, 0.7f, 0.7f);
                text.text = Path.GetFileName(row.Path);
                text.style.unityFontStyleAndWeight = FontStyle.Bold;
                text.style.color = theme.DefaultText;
                text.style.paddingLeft = 0;
                string counts = (row.Errors > 0 ? $"\u2716 {row.Errors}  " : "") + (row.Warnings > 0 ? $"\u26A0 {row.Warnings}  " : "");
                detail.text = counts + Path.GetDirectoryName(row.Path)?.Replace('\\', '/');
            }
            else
            {
                icon.text = row.Item.IsError ? "\u2716" : "\u26A0";
                icon.style.color = row.Item.IsError ? new Color(1f, 0.35f, 0.35f) : new Color(1f, 0.8f, 0.25f);
                text.text = row.Item.Message;
                text.style.unityFontStyleAndWeight = FontStyle.Normal;
                text.style.color = theme.DefaultText;
                text.style.paddingLeft = 16;
                detail.text = $"Ln {row.Item.Line}, Col {row.Item.Column}";
            }
            element.tooltip = row.IsHeader ? row.Path : row.Item.Message;
        }

        private void OpenProblem(ProblemRow row)
        {
            if (row == null || row.IsHeader) return;
            LoadFileAndJumpToLine(row.Path, Mathf.Max(1, row.Item.Line));
        }

        private void ShowProblems()
        {
            if (m_ProblemsPanel != null && m_ProblemsPanel.style.display == DisplayStyle.None) ToggleProblems();
        }

        private void ToggleProblems()
        {
            if (m_ProblemsPanel == null) return;
            bool show = m_ProblemsPanel.style.display == DisplayStyle.None;
            m_ProblemsPanel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show) RefreshProblems();
            else m_ActivePane?.Viewer?.Focus();
        }

        private void RefreshProblems()
        {
            if (m_ProblemsPanel == null || m_ProblemsPanel.style.display == DisplayStyle.None) return;

            bool errors = m_ProblemsErrors.value, warnings = m_ProblemsWarnings.value;
            string filter = m_ProblemsFilter.value?.Trim() ?? "";
            string current = m_ActivePane?.ActiveTab != null ? ProjectDiagnosticsService.NormalizeAssetPath(m_ActivePane.ActiveTab) : null;

            m_ProblemRows.Clear();
            int totalErrors = 0, totalWarnings = 0;
            foreach (var file in ProjectDiagnosticsService.GetAllDiagnostics().OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (m_ProblemsCurrentFile.value && !string.Equals(file.Key, current, StringComparison.OrdinalIgnoreCase)) continue;
                bool fileMatches = filter.Length == 0 || file.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                var items = file.Value.Items
                    .Where(i => i.IsError ? errors : warnings)
                    .Where(i => fileMatches || i.Message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(i => i.IsError ? 0 : 1).ThenBy(i => i.Line)
                    .ToList();
                if (items.Count == 0) continue;

                var header = new ProblemRow { Path = file.Key, IsHeader = true, Errors = items.Count(i => i.IsError), Warnings = items.Count(i => !i.IsError) };
                totalErrors += header.Errors;
                totalWarnings += header.Warnings;
                m_ProblemRows.Add(header);
                if (m_CollapsedProblemFiles.Contains(file.Key)) continue;
                foreach (var item in items) m_ProblemRows.Add(new ProblemRow { Path = file.Key, Item = item });
            }

            m_ProblemsTitle.text = $"Problems  \u2716 {totalErrors}  \u26A0 {totalWarnings}";
            m_ProblemsList.RefreshItems();
        }
    }
}
