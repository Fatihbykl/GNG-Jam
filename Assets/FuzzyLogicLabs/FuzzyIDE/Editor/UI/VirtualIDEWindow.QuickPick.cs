using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>
    /// The Ctrl+P quick pick: fuzzy file search, plus VS Code style prefixes —
    /// "#" workspace symbols, "@" symbols of the current file, ":" go to line, ">" commands.
    /// Also owns the Alt+Left / Alt+Right navigation history.
    /// </summary>
    public partial class VirtualIDEWindow
    {
        private const int MaxQuickPickItems = 200;

        private sealed class QuickPickItem
        {
            public string Label;
            public string Detail;
            public string Path;
            public int Line = -1;   // 0-based
            public int Column;
            public Action Run;
        }

        private readonly List<QuickPickItem> m_QuickPickItems = new List<QuickPickItem>();
        private List<CodeSymbol> m_QuickPickFileSymbols;
        private string m_QuickPickFileSymbolsPath;

        private readonly NavigationHistory m_NavHistory = new NavigationHistory { Capacity = 100 };
        private bool m_NavigatingHistory;

        private void ShowQuickPick(string prefix)
        {
            m_QuickPickFileSymbols = null;
            m_QuickSearchPopup.style.display = DisplayStyle.Flex;
            m_QuickSearchInput.value = prefix ?? "";
            UpdateQuickPick(m_QuickSearchInput.value);
            m_QuickSearchInput.Focus();
            int caret = (prefix ?? "").Length;
            m_QuickSearchInput.schedule.Execute(() => m_QuickSearchInput.SelectRange(caret, caret)).StartingIn(10);
        }

        private void BindQuickPickItem(VisualElement element, int index)
        {
            var label = (Label)element;
            var item = m_QuickPickItems[index];
            string detail = string.IsNullOrEmpty(item.Detail) ? "" : $"   <color=#888888>{item.Detail.Replace("<", "<\u200B")}</color>";
            label.text = item.Label.Replace("<", "<\u200B") + detail;
            label.tooltip = item.Path;
        }

        private void AcceptQuickPick(QuickPickItem item)
        {
            CloseQuickSearch();
            if (item == null) return;
            if (item.Run != null)
            {
                item.Run();
                return;
            }
            if (item.Path == null) return;

            LoadFileAndJumpToLine(item.Path, item.Line >= 0 ? item.Line + 1 : -1);
            if (item.Line >= 0) m_ActivePane?.Viewer?.SetCaretPosition(item.Line, item.Column);
        }

        private void UpdateQuickPick(string query)
        {
            m_QuickPickItems.Clear();
            query = query ?? "";

            if (query.StartsWith(">")) AddCommandItems(query.Substring(1).Trim());
            else if (query.StartsWith("#")) AddWorkspaceSymbolItems(query.Substring(1).Trim());
            else if (query.StartsWith("@")) AddFileSymbolItems(query.Substring(1).Trim());
            else if (query.StartsWith(":")) AddGoToLineItem(query.Substring(1).Trim());
            else AddFileItems(query.Trim());

            m_QuickSearchList.RefreshItems();
            if (m_QuickPickItems.Count > 0)
            {
                m_QuickSearchList.selectedIndex = 0;
                m_QuickSearchList.ScrollToItem(0);
            }
        }

        // Adds the best fuzzy matches; an empty query keeps the source order.
        private void AddRanked<T>(IEnumerable<T> source, string query, Func<T, string> key, Func<T, QuickPickItem> toItem)
        {
            if (string.IsNullOrEmpty(query))
            {
                m_QuickPickItems.AddRange(source.Take(MaxQuickPickItems).Select(toItem));
                return;
            }

            var ranked = new List<KeyValuePair<int, T>>();
            foreach (var entry in source)
            {
                int score = FuzzyMatcher.Score(query, key(entry));
                if (score != FuzzyMatcher.NoMatch) ranked.Add(new KeyValuePair<int, T>(score, entry));
            }
            m_QuickPickItems.AddRange(ranked.OrderByDescending(r => r.Key).Take(MaxQuickPickItems).Select(r => toItem(r.Value)));
        }

        private void AddFileItems(string query)
        {
            // Recently visited files first when the query is empty.
            var recent = GetRecentFiles();
            IEnumerable<string> files = string.IsNullOrEmpty(query)
                ? recent.Concat(m_ProjectScripts.Where(p => !recent.Contains(p)))
                : m_ProjectScripts;

            AddRanked(files, query, Path.GetFileName, path => new QuickPickItem
            {
                Label = Path.GetFileName(path),
                Detail = Path.GetDirectoryName(path)?.Replace('\\', '/'),
                Path = path
            });
        }

        private List<string> GetRecentFiles()
        {
            var recent = new List<string>();
            foreach (var tab in new[] { m_ActivePane?.ActiveTab }.Concat(m_LeftPane?.OpenedTabs ?? new List<string>()).Concat(m_RightPane?.OpenedTabs ?? new List<string>()))
            {
                if (!string.IsNullOrEmpty(tab) && !recent.Contains(tab)) recent.Add(tab);
            }
            return recent;
        }

        private static string SymbolKindLabel(CodeSymbolKind kind)
        {
            switch (kind)
            {
                case CodeSymbolKind.Method: return "method";
                case CodeSymbolKind.Constructor: return "ctor";
                case CodeSymbolKind.Property: return "property";
                case CodeSymbolKind.Field: return "field";
                case CodeSymbolKind.Event: return "event";
                case CodeSymbolKind.EnumMember: return "enum member";
                default: return kind.ToString().ToLowerInvariant();
            }
        }

        private QuickPickItem SymbolItem(CodeSymbol s, bool withFile)
        {
            string assetPath = ProjectDiagnosticsService.NormalizeAssetPath(s.FilePath);
            string container = string.IsNullOrEmpty(s.Container) ? "" : s.Container + "  ";
            return new QuickPickItem
            {
                Label = s.Name,
                Detail = container + SymbolKindLabel(s.Kind) + (withFile ? "  " + Path.GetFileName(assetPath) : $"  :{s.Line + 1}"),
                Path = assetPath,
                Line = s.Line,
                Column = s.Column
            };
        }

        private void AddWorkspaceSymbolItems(string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                m_QuickPickItems.Add(new QuickPickItem { Label = "Type to search classes, methods, fields...", Detail = "workspace symbols" });
                return;
            }
            var symbols = SymbolIndex.GetWorkspaceSymbols();
            if (symbols.Count == 0)
            {
                m_QuickPickItems.Add(new QuickPickItem { Label = "Indexing project...", Detail = "try again in a moment" });
                return;
            }
            AddRanked(symbols, query, s => s.Name, s => SymbolItem(s, withFile: true));
        }

        private void AddFileSymbolItems(string query)
        {
            var pane = m_ActivePane;
            if (pane == null || string.IsNullOrEmpty(pane.ActiveTab) || !pane.ActiveTab.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

            // Parse the buffer once per popup session; the query only filters.
            if (m_QuickPickFileSymbols == null || m_QuickPickFileSymbolsPath != pane.ActiveTab)
            {
                m_QuickPickFileSymbols = SymbolIndex.GetSymbols(pane.Viewer.GetText(), pane.ActiveTab);
                m_QuickPickFileSymbolsPath = pane.ActiveTab;
            }
            AddRanked(m_QuickPickFileSymbols, query, s => s.Name, s => SymbolItem(s, withFile: false));
        }

        private void AddGoToLineItem(string query)
        {
            var pane = m_ActivePane;
            if (pane?.Viewer == null || string.IsNullOrEmpty(pane.ActiveTab)) return;
            int lineCount = pane.Viewer.GetLines().Count;

            if (!int.TryParse(query, out int line))
            {
                m_QuickPickItems.Add(new QuickPickItem { Label = $"Type a line number between 1 and {lineCount}" });
                return;
            }
            line = Mathf.Clamp(line, 1, lineCount);
            m_QuickPickItems.Add(new QuickPickItem { Label = $"Go to line {line}", Path = pane.ActiveTab, Line = line - 1 });
        }

        private void AddCommandItems(string query)
        {
            AddRanked(GetCommands(), query, c => c.Label, c => c);
        }

        private static string ShortcutText(IDEAction action)
        {
            var binding = CodeIDESettings.Current.Keybindings.Bindings.Find(b => b.Action == action)?.Binding;
            return binding == null ? "" : binding.ToString().Replace("Control", "Ctrl").Replace(", ", "+");
        }

        private QuickPickItem Command(string label, Action run, IDEAction? shortcut = null) =>
            new QuickPickItem { Label = label, Detail = shortcut.HasValue ? ShortcutText(shortcut.Value) : null, Run = run };

        private QuickPickItem EditorCommand(string label, IDEAction action) =>
            Command(label, () => m_ActivePane?.Viewer?.ExecuteAction(action), action);

        private List<QuickPickItem> GetCommands()
        {
            return new List<QuickPickItem>
            {
                Command("File: Save", () => SaveCurrentActiveFile(null), IDEAction.Save),
                Command("File: Save All", SaveAll),
                Command("Go to File...", () => ShowQuickPick(""), IDEAction.QuickSearch),
                Command("Go to Symbol in Workspace...", () => ShowQuickPick("#"), IDEAction.GoToSymbol),
                Command("Go to Symbol in File...", () => ShowQuickPick("@"), IDEAction.GoToFileSymbol),
                Command("Go to Line...", () => ShowQuickPick(":"), IDEAction.GoToLine),
                Command("Search: Find in Files", ShowFindInFiles, IDEAction.FindInFiles),
                Command("View: Problems", ToggleProblems, IDEAction.ToggleProblems),
                Command("Navigate Back", NavigateBack, IDEAction.NavigateBack),
                Command("Navigate Forward", NavigateForward, IDEAction.NavigateForward),
                EditorCommand("Edit: Find", IDEAction.Find),
                EditorCommand("Edit: Format Document", IDEAction.FormatDocument),
                EditorCommand("Code: Cleanup (Remove Unused Usings, Sort, Format)", IDEAction.CodeCleanup),
                EditorCommand("Edit: Toggle Line Comment", IDEAction.ToggleLineComment),
                EditorCommand("Edit: Duplicate Line", IDEAction.DuplicateLine),
                EditorCommand("Edit: Insert Line Below", IDEAction.InsertLineBelow),
                EditorCommand("Edit: Insert Line Above", IDEAction.InsertLineAbove),
                EditorCommand("Edit: Undo", IDEAction.Undo),
                EditorCommand("Edit: Redo", IDEAction.Redo),
                EditorCommand("Code: Go to Definition", IDEAction.GoToDefinition),
                EditorCommand("Code: Find All References", IDEAction.FindReferences),
                EditorCommand("Code: Rename Symbol", IDEAction.SemanticRename),
                EditorCommand("View: Zoom In", IDEAction.ZoomIn),
                EditorCommand("View: Zoom Out", IDEAction.ZoomOut),
                EditorCommand("View: Reset Zoom", IDEAction.ZoomReset),
                Command("View: Toggle Explorer", ToggleExplorer),
                Command("View: Toggle Split Editor", ToggleSplitView),
                Command("Unity: Recompile Scripts", Recompile, IDEAction.Recompile),
                Command("Unity: Reload Domain", ReloadDomain, IDEAction.ReloadDomain),
                Command("Open: Fuzzy Notes", Notes.UI.FuzzyNotesWindow.ShowWindow, IDEAction.OpenNotes),
                Command("Open: Git", Git.GitWindow.ShowWindow),
                Command("Open: Terminal", Terminal.NativeTerminalWindow.ShowWindow),
                Command("Open: Snippets File", OpenUserSnippets),
                Command("Preferences: Fuzzy IDE Settings", () => SettingsService.OpenUserPreferences("Preferences/Fuzzy Logic Labs/Code IDE")),
            };
        }

        private void OpenUserSnippets()
        {
            string path = SnippetLibrary.UserSnippetsPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path,
                    "{\n" +
                    "    // Snippets in VS Code format. $1, ${2:placeholder} are tab stops, $0 is the final caret position.\n" +
                    "    \"Log value\": {\n" +
                    "        \"prefix\": \"logv\",\n" +
                    "        \"body\": [\"Debug.Log($\\\"${1:name} = {${2:value}}\\\");$0\"],\n" +
                    "        \"description\": \"Log a value with its name\"\n" +
                    "    }\n" +
                    "}\n");
            }
            LoadFileAndJumpToLine(path, 1);
        }

        // ---- Navigation history -------------------------------------------------------------------------------

        private static string NavKey(string path, int line) => path + "\n" + line;

        private void RecordLocation(string path, int line)
        {
            if (m_NavigatingHistory || string.IsNullOrEmpty(path) || line < 0) return;
            m_NavHistory.Visit(NavKey(path, line), null);
        }

        private void RecordCurrentLocation()
        {
            var pane = m_ActivePane;
            if (pane?.Viewer == null || string.IsNullOrEmpty(pane.ActiveTab)) return;
            RecordLocation(pane.ActiveTab, pane.Viewer.CaretLine);
        }

        private void OnViewerJumpRequested(EditorPaneState pane, int fromLine, int toLine)
        {
            // LoadFileAndJumpToLine records both ends itself; the viewer's own jump would add the restored caret line.
            if (m_InLoadJump || string.IsNullOrEmpty(pane.ActiveTab)) return;
            RecordLocation(pane.ActiveTab, fromLine);
            RecordLocation(pane.ActiveTab, toLine);
        }

        private void NavigateBack() => NavigateHistory(back: true);
        private void NavigateForward() => NavigateHistory(back: false);

        private void NavigateHistory(bool back)
        {
            // Make sure the place we leave is reachable with Forward.
            if (back) RecordCurrentLocation();

            var entry = back ? m_NavHistory.GoBack() : m_NavHistory.GoForward();
            if (entry == null) return;

            int split = entry.Key.LastIndexOf('\n');
            string path = entry.Key.Substring(0, split);
            int line = int.Parse(entry.Key.Substring(split + 1));
            if (!File.Exists(path))
            {
                m_NavHistory.Remove(e => e.Key.StartsWith(path + "\n", StringComparison.Ordinal));
                return;
            }

            m_NavigatingHistory = true;
            try
            {
                LoadFileAndJumpToLine(path, line + 1);
            }
            finally
            {
                m_NavigatingHistory = false;
            }
        }
    }
}
