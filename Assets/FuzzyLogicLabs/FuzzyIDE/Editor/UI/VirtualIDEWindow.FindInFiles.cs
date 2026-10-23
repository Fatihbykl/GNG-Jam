using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>Project-wide Find / Replace in Files panel (Ctrl+Shift+F).</summary>
    public partial class VirtualIDEWindow
    {
        private const int MaxFindResults = 5000;

        private sealed class FindResultRow
        {
            public bool IsFile;
            public string Path;
            public int Count;
            public TextSearchMatch Match;
        }

        private VisualElement m_FindPanel;
        private TextField m_FindQueryField;
        private TextField m_FindReplaceField;
        private TextField m_FindIncludeField;
        private Toggle m_FindMatchCase, m_FindWholeWord, m_FindRegex;
        private Label m_FindStatus;
        private ListView m_FindResultsList;
        private readonly List<FindResultRow> m_FindRows = new List<FindResultRow>();
        private readonly HashSet<string> m_CollapsedFindFiles = new HashSet<string>();
        private List<TextSearchMatch> m_FindMatches = new List<TextSearchMatch>();
        private CancellationTokenSource m_FindCts;
        private IVisualElementScheduledItem m_FindDebounce;
        private int m_FindVersion;

        private void InitializeFindInFilesUI()
        {
            var theme = CodeIDESettings.Current.Theme;

            m_FindPanel = new VisualElement { name = "find-in-files" };
            m_FindPanel.style.height = 260;
            m_FindPanel.style.backgroundColor = theme.Background;
            m_FindPanel.style.borderTopWidth = 2;
            m_FindPanel.style.borderTopColor = theme.UIBorder;
            m_FindPanel.style.display = DisplayStyle.None;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.backgroundColor = theme.UIPopupBackground;
            header.style.paddingLeft = 6; header.style.paddingRight = 4;

            var title = new Label("Find in Files");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginRight = 8;
            header.Add(title);

            m_FindQueryField = new TextField { tooltip = "Search text (Enter: search now, Esc: close)" };
            m_FindQueryField.style.flexGrow = 1;
            header.Add(m_FindQueryField);

            m_FindMatchCase = MakeFindToggle("Aa", "Match case");
            m_FindWholeWord = MakeFindToggle("W", "Match whole word");
            m_FindRegex = MakeFindToggle(".*", "Use regular expression");
            header.Add(m_FindMatchCase);
            header.Add(m_FindWholeWord);
            header.Add(m_FindRegex);

            m_FindIncludeField = new TextField { tooltip = "Files to include, comma separated: *.cs, Scripts/, !Tests/" };
            m_FindIncludeField.style.width = 180;
            header.Add(m_FindIncludeField);

            var close = new Button(CloseFindInFiles) { text = "X", tooltip = "Close" };
            close.style.backgroundColor = Color.clear;
            close.style.borderTopWidth = 0; close.style.borderBottomWidth = 0;
            close.style.borderLeftWidth = 0; close.style.borderRightWidth = 0;
            header.Add(close);
            m_FindPanel.Add(header);

            var replaceRow = new VisualElement();
            replaceRow.style.flexDirection = FlexDirection.Row;
            replaceRow.style.alignItems = Align.Center;
            replaceRow.style.paddingLeft = 6; replaceRow.style.paddingRight = 4;
            var replaceLabel = new Label("Replace");
            replaceLabel.style.width = 78;
            replaceRow.Add(replaceLabel);
            m_FindReplaceField = new TextField { tooltip = "Replacement (regex mode supports $1 groups)" };
            m_FindReplaceField.style.flexGrow = 1;
            replaceRow.Add(m_FindReplaceField);
            replaceRow.Add(new Button(ReplaceAllInFiles) { text = "Replace All" });
            m_FindPanel.Add(replaceRow);

            m_FindStatus = new Label();
            m_FindStatus.style.paddingLeft = 8;
            m_FindStatus.style.color = new Color(0.65f, 0.65f, 0.65f);
            m_FindPanel.Add(m_FindStatus);

            m_FindResultsList = new ListView
            {
                itemsSource = m_FindRows,
                selectionType = SelectionType.Single,
                fixedItemHeight = 20,
                makeItem = () =>
                {
                    var label = new Label { enableRichText = true };
                    label.style.unityTextAlign = TextAnchor.MiddleLeft;
                    label.style.whiteSpace = WhiteSpace.NoWrap;
                    label.style.overflow = Overflow.Hidden;
                    return label;
                },
                bindItem = BindFindRow
            };
            m_FindResultsList.style.flexGrow = 1;
            m_FindResultsList.selectionChanged += _ => OpenSelectedFindResult(takeFocus: false);
            m_FindResultsList.itemsChosen += _ => OpenSelectedFindResult(takeFocus: true);
            m_FindPanel.Add(m_FindResultsList);

            m_FindQueryField.RegisterValueChangedCallback(_ => ScheduleFindInFiles());
            m_FindIncludeField.RegisterValueChangedCallback(_ => ScheduleFindInFiles());
            m_FindPanel.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    CloseFindInFiles();
                    e.StopPropagation();
                }
                else if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && e.target is VisualElement ve && IsInside(ve, m_FindQueryField))
                {
                    RunFindInFiles();
                    e.StopPropagation();
                }
            }, TrickleDown.TrickleDown);

            rootVisualElement.Add(m_FindPanel);
        }

        private Toggle MakeFindToggle(string text, string tooltip)
        {
            var toggle = new Toggle { text = text, tooltip = tooltip };
            toggle.style.marginLeft = 4;
            toggle.RegisterValueChangedCallback(_ => ScheduleFindInFiles());
            return toggle;
        }

        private static bool IsInside(VisualElement element, VisualElement container)
        {
            for (var e = element; e != null; e = e.parent)
                if (e == container) return true;
            return false;
        }

        /// <summary>Opens the panel, prefilled with the editor's single-line selection.</summary>
        private void ShowFindInFiles()
        {
            if (m_FindPanel == null) return;
            m_FindPanel.style.display = DisplayStyle.Flex;

            string selection = m_ActivePane?.Viewer?.GetSingleLineSelection();
            if (!string.IsNullOrEmpty(selection)) m_FindQueryField.value = selection;

            m_FindQueryField.schedule.Execute(() =>
            {
                m_FindQueryField.Focus();
                m_FindQueryField.SelectAll();
            }).StartingIn(30);
            ScheduleFindInFiles();
        }

        private void CloseFindInFiles()
        {
            m_FindCts?.Cancel();
            m_FindPanel.style.display = DisplayStyle.None;
            m_ActivePane?.Viewer?.Focus();
        }

        private TextSearchOptions CurrentFindOptions() => new TextSearchOptions
        {
            Query = m_FindQueryField.value ?? "",
            MatchCase = m_FindMatchCase.value,
            WholeWord = m_FindWholeWord.value,
            UseRegex = m_FindRegex.value,
            Include = m_FindIncludeField.value ?? ""
        };

        private void ScheduleFindInFiles()
        {
            m_FindDebounce?.Pause();
            m_FindDebounce = rootVisualElement.schedule.Execute(RunFindInFiles).StartingIn(250);
        }

        // Unsaved buffers win over disk so results match what the editor shows.
        private Dictionary<string, string> SnapshotOpenBuffers()
        {
            var buffers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in m_OpenedFiles) buffers[kvp.Key] = string.Join("\n", kvp.Value.Lines);
            return buffers;
        }

        private void RunFindInFiles()
        {
            m_FindDebounce?.Pause();
            m_FindCts?.Cancel();
            var options = CurrentFindOptions();

            if (string.IsNullOrEmpty(options.Query))
            {
                m_FindMatches = new List<TextSearchMatch>();
                RebuildFindRows();
                m_FindStatus.text = "";
                return;
            }

            ProjectTextSearch.BuildPattern(options, out string error);
            if (error != null)
            {
                m_FindStatus.text = "Invalid regular expression: " + error;
                return;
            }

            var cts = new CancellationTokenSource();
            m_FindCts = cts;
            int version = ++m_FindVersion;
            var files = m_ProjectScripts.ToList();
            var buffers = SnapshotOpenBuffers();
            m_FindStatus.text = "Searching...";

            Task.Run(() =>
            {
                var matches = ProjectTextSearch.Search(files,
                    path => buffers.TryGetValue(path, out var text) ? text : File.ReadAllText(path),
                    options, MaxFindResults, cts.Token, out bool truncated);
                return (matches, truncated);
            }, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled || t.IsFaulted || version != m_FindVersion) return;
                var (matches, truncated) = t.Result;
                // Back on the main thread for the UI.
                EditorApplication.delayCall += () =>
                {
                    if (version != m_FindVersion || m_FindPanel == null) return;
                    m_FindMatches = matches;
                    RebuildFindRows();
                    int fileCount = matches.Select(m => m.FilePath).Distinct().Count();
                    m_FindStatus.text = matches.Count == 0
                        ? "No results"
                        : $"{matches.Count}{(truncated ? "+" : "")} results in {fileCount} files" + (truncated ? " (refine the search)" : "");
                };
            });
        }

        private void RebuildFindRows()
        {
            m_FindRows.Clear();
            foreach (var group in m_FindMatches.GroupBy(m => m.FilePath))
            {
                var list = group.ToList();
                m_FindRows.Add(new FindResultRow { IsFile = true, Path = group.Key, Count = list.Count });
                if (m_CollapsedFindFiles.Contains(group.Key)) continue;
                foreach (var match in list) m_FindRows.Add(new FindResultRow { Path = group.Key, Match = match });
            }
            m_FindResultsList.RefreshItems();
        }

        private void BindFindRow(VisualElement element, int index)
        {
            var label = (Label)element;
            var row = m_FindRows[index];
            if (row.IsFile)
            {
                string arrow = m_CollapsedFindFiles.Contains(row.Path) ? "▶" : "▼";
                string dir = Path.GetDirectoryName(row.Path)?.Replace('\\', '/');
                label.text = $"{arrow} <b>{Path.GetFileName(row.Path)}</b>  <color=#888888>{dir}  ({row.Count})</color>";
                label.style.paddingLeft = 4;
                label.tooltip = row.Path;
                return;
            }

            var m = row.Match;
            string text = m.LineText ?? "";
            // Trim long indentation so the match stays in view.
            int trim = 0;
            while (trim < text.Length && trim < m.Column && char.IsWhiteSpace(text[trim])) trim++;
            int start = m.Column - trim;
            string visible = text.Substring(trim);
            int end = Mathf.Min(visible.Length, start + m.Length);
            var sb = new StringBuilder();
            sb.Append("<color=#888888>").Append(m.Line + 1).Append(":</color>  ");
            sb.Append(Escape(visible.Substring(0, start)));
            sb.Append("<b><color=#FFD866>").Append(Escape(visible.Substring(start, end - start))).Append("</color></b>");
            sb.Append(Escape(visible.Substring(end)));
            label.text = sb.ToString();
            label.style.paddingLeft = 22;
            label.tooltip = null;
        }

        private static string Escape(string text) => text.Replace("<", "<\u200B");

        private void OpenSelectedFindResult(bool takeFocus)
        {
            int index = m_FindResultsList.selectedIndex;
            if (index < 0 || index >= m_FindRows.Count) return;
            var row = m_FindRows[index];

            if (row.IsFile)
            {
                if (!takeFocus) return;
                if (!m_CollapsedFindFiles.Remove(row.Path)) m_CollapsedFindFiles.Add(row.Path);
                RebuildFindRows();
                return;
            }

            LoadFileAndJumpToLine(row.Path, row.Match.Line + 1);
            m_ActivePane?.Viewer?.SelectRange(row.Match.Line, row.Match.Column, row.Match.Column + row.Match.Length);
            if (!takeFocus) m_FindResultsList.Focus();
        }

        private void ReplaceAllInFiles()
        {
            var options = CurrentFindOptions();
            if (string.IsNullOrEmpty(options.Query) || m_FindMatches.Count == 0) return;

            string replacement = m_FindReplaceField.value ?? "";
            var files = m_FindMatches.Select(m => m.FilePath).Distinct().ToList();
            if (!EditorUtility.DisplayDialog("Replace All",
                    $"Replace {m_FindMatches.Count} matches in {files.Count} files with \"{replacement}\"?\n\nOpen files can be undone in the editor; closed files are written to disk.",
                    "Replace", "Cancel"))
                return;

            int total = 0;
            foreach (var file in files)
            {
                try
                {
                    if (m_OpenedFiles.TryGetValue(file, out var state))
                    {
                        string oldText = string.Join("\n", state.Lines);
                        string newText = ProjectTextSearch.Replace(oldText, options, replacement, out int count);
                        if (count == 0) continue;
                        ReplaceBufferText(file, state, newText);
                        total += count;
                    }
                    else
                    {
                        string full = Path.GetFullPath(file);
                        string oldText = File.ReadAllText(full);
                        string newText = ProjectTextSearch.Replace(oldText, options, replacement, out int count);
                        if (count == 0) continue;
                        File.WriteAllText(full, newText);
                        CompilationManager.MarkFileModified(file);
                        total += count;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FuzzyIDE] Replace in {file} failed: {ex.Message}");
                }
            }

            m_FindStatus.text = $"Replaced {total} matches in {files.Count} files";
            AssetDatabase.Refresh();
            ScheduleFindInFiles();
        }

        // Replaces an open buffer's whole text as one undoable edit (same approach as semantic rename).
        private void ReplaceBufferText(string assetPath, FileState state, string newText)
        {
            var delCmd = new DeleteSelectionCommand(state.Lines, 0, 0, state.Lines.Count - 1, state.Lines[state.Lines.Count - 1].Length, (l, c) => { });
            var insCmd = new InsertTextCommand(state.Lines, newText, 0, 0, (l, c) => { });
            var composite = new CompositeCommand(new List<IEditCommand> { delCmd, insCmd });
            composite.Execute();
            state.IsDirty = true;
            // The active viewer uses state.History itself, so one AddCommand covers both.
            state.History.AddCommand(composite);

            foreach (var pane in new[] { m_LeftPane, m_RightPane })
            {
                if (pane == null || pane.ActiveTab != assetPath) continue;
                pane.Viewer.SetLines(state.Lines, false);
                RefreshTabBar(pane);
            }
            UpdateWindowTitle();
        }
    }
}
