using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Fuzzy Notes window, laid out like Obsidian: ribbon | left sidebar (files, search, bookmarks, tags) |
    /// split-pane workspace | right sidebar (outline, backlinks). Sidebars are resizable and collapsible.
    /// Every action is a <see cref="NoteCommand"/>: the palette, the ribbon and the hotkeys all use the same registry.
    /// </summary>
    public class FuzzyNotesWindow : EditorWindow
    {
        private const string LeftWidthPref = "FuzzyNotes.LeftSidebarWidth";
        private const string RightWidthPref = "FuzzyNotes.RightSidebarWidth";
        private const string LeftCollapsedPref = "FuzzyNotes.LeftSidebarCollapsed";
        private const string RightCollapsedPref = "FuzzyNotes.RightSidebarCollapsed";
        private const string FocusModeSessionKey = "FuzzyNotes.FocusMode";
        private const string RecentCommandsPref = "FuzzyNotes.RecentCommands";

        private FileExplorerView m_ListPanel;
        private SearchView m_Search;
        private NoteCommandRegistry m_Commands;
        private WorkspaceView m_Workspace;
        private SidebarView m_LeftSidebar;
        private SidebarView m_RightSidebar;
        private TwoPaneSplitView m_OuterSplit;
        private TwoPaneSplitView m_InnerSplit;
        private VisualElement m_Ribbon;
        private bool m_FocusMode;
        private Button m_LeftToggle;
        private Button m_RightToggle;
        private NoteOutlinePanel m_Outline;
        private BacklinksPanel m_Backlinks;
        private GraphView m_LocalGraph;
        private NoteEditorView m_BoundEditor;
        private NoteItem m_BoundNote;

        private NotePalette m_Palette;

        /// <summary>Editor of the active pane when it shows a note.</summary>
        private NoteEditorView ActiveEditor => m_Workspace?.ActiveEditor;

        [MenuItem("Window/Fuzzy Logic Labs/Fuzzy IDE/Fuzzy Notes", false, 22)]
        public static void ShowWindow()
        {
            var window = GetWindow<FuzzyNotesWindow>();
            window.titleContent = new GUIContent("Fuzzy Notes", EditorGUIUtility.IconContent("TextAsset Icon").image);
            window.minSize = new Vector2(650, 400);
            window.Show();
        }

        /// <summary>
        /// Shows the window with <paramref name="note"/> open (in the active pane, or a new pane next to it) and,
        /// when <paramref name="bodyLine"/> ≥ 0, scrolled to that body line.
        /// </summary>
        public static void OpenNote(NoteItem note, int bodyLine = -1, NoteOpenMode mode = NoteOpenMode.Default)
        {
            if (note == null) return;
            ShowWindow();
            var window = GetWindow<FuzzyNotesWindow>();
            if (window.m_Workspace != null) window.m_Workspace.Open(note, mode);
            else NoteStore.Instance.SetActiveNote(note); // CreateGUI shows the active note
            if (bodyLine < 0) return;
            // The editor lays out its lines on the next frame.
            EditorApplication.delayCall += () =>
            {
                var editor = window != null ? window.ActiveEditor : null;
                if (editor != null && editor.CurrentNote == note) editor.RevealBodyLine(bodyLine);
            };
        }

        private void OnEnable()
        {
            NoteStore.Instance.LoadAllNotes();
            CodeIDESettings.OnSettingsChanged += OnSettingsChanged;
            NoteSettings.ThemeChanged += OnSettingsChanged;
            NoteSettings.AppearanceChanged += UpdateRibbonVisibility;
        }

        private void OnDisable()
        {
            CodeIDESettings.OnSettingsChanged -= OnSettingsChanged;
            NoteSettings.ThemeChanged -= OnSettingsChanged;
            NoteSettings.AppearanceChanged -= UpdateRibbonVisibility;
            m_Workspace?.SaveNow();
            NoteStore.Instance.FlushPendingSaves();
        }

        private void OnFocus()
        {
            // Catches edits the file watcher cannot see (external files, dropped events).
            NoteStore.Instance.CheckExternalChanges();
        }

        private void CreateGUI()
        {
            // CreateGUI also rebuilds the window after a theme change: drop the root callbacks first.
            rootVisualElement.UnregisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);
            rootVisualElement.UnregisterCallback<DragUpdatedEvent>(OnRootDragUpdated, TrickleDown.TrickleDown);
            rootVisualElement.UnregisterCallback<DragPerformEvent>(OnRootDragPerform, TrickleDown.TrickleDown);
            m_Workspace?.SaveNow();
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Row;
            NotesTheme.Apply(rootVisualElement);

            m_Commands = BuildCommandRegistry();
            m_Ribbon = BuildRibbon();
            rootVisualElement.Add(m_Ribbon);
            UpdateRibbonVisibility();

            // Left sidebar: files, bookmarks, tags
            m_ListPanel = new FileExplorerView();
            m_ListPanel.OnNewNoteClicked += HandleCreateNewNote;
            m_ListPanel.OnOpenExternalClicked += HandleOpenExternalFile;
            m_ListPanel.OnMoveNotesRequested += ShowMovePicker;

            m_LeftSidebar = new SidebarView("FuzzyNotes.LeftPane");
            m_LeftSidebar.AddPane("files", "folder", "Files", m_ListPanel);
            m_Search = new SearchView();
            m_Search.OnOpenResult += (note, line, mode) => OpenNote(note, line, mode);
            m_Search.OnRevealInExplorer += RevealInFiles;
            m_LeftSidebar.AddPane("search", "search", $"Search ({Shortcut(IDEAction.NoteSearch)})", m_Search);
            var bookmarks = new BookmarksPanel();
            m_LeftSidebar.AddPane("bookmarks", "bookmark", "Bookmarks", bookmarks, bookmarks.SetVisible);
            var tags = new TagsPanel(tag => ShowSearch("tag:" + tag));
            m_LeftSidebar.AddPane("tags", "tags", "Tags", tags, tags.SetVisible);

            // Workspace
            m_Workspace = new WorkspaceView();
            m_Workspace.ActiveContentChanged += BindRightSidebar;
            m_Workspace.ShowSidebarPaneRequested += ShowRightPane;
            m_Workspace.ExecuteCommandRequested += HandleExecuteCommand;
            m_Workspace.QuickSwitcherRequested += ShowQuickSwitcher;
            m_Workspace.EmptyStateActionRequested += action =>
            {
                if (action == WorkspaceView.EmptyStateAction.NewNote) HandleCreateNewNote();
                else OpenDailyNote();
            };
            m_Workspace.RevealInExplorerRequested += RevealInFiles;
            m_Workspace.InsertTemplateRequested += () => ShowTemplatePicker(asNewNote: false);
            m_Workspace.SearchRequested += query => ShowSearch(query);
            m_Workspace.Prompt = (placeholder, onSubmit) => m_Palette.OpenPrompt(placeholder, null, onSubmit);

            // Right sidebar: outline, backlinks
            m_RightSidebar = new SidebarView("FuzzyNotes.RightPane");
            m_Outline = new NoteOutlinePanel();
            m_RightSidebar.AddPane("outline", "list", "Outline", m_Outline);
            m_Backlinks = new BacklinksPanel();
            m_RightSidebar.AddPane("backlinks", "link", "Backlinks", m_Backlinks, m_Backlinks.SetVisible);
            m_LocalGraph = new GraphView(local: true);
            m_LocalGraph.OnOpenNote += (note, mode) => m_Workspace.Open(note, mode);
            m_LocalGraph.OnSearchRequested += query => ShowSearch(query);
            m_RightSidebar.AddPane("localgraph", "waypoints", "Local graph", m_LocalGraph, m_LocalGraph.SetVisible);

            m_InnerSplit = new TwoPaneSplitView(1, EditorPrefs.GetFloat(RightWidthPref, 260f), TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            m_InnerSplit.Add(m_Workspace);
            m_InnerSplit.Add(m_RightSidebar);
            m_OuterSplit = new TwoPaneSplitView(0, EditorPrefs.GetFloat(LeftWidthPref, 270f), TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            m_OuterSplit.Add(m_LeftSidebar);
            m_OuterSplit.Add(m_InnerSplit);
            rootVisualElement.Add(m_OuterSplit);

            TrackWidth(m_LeftSidebar, LeftWidthPref);
            TrackWidth(m_RightSidebar, RightWidthPref);
            m_LeftSidebar.style.borderRightWidth = 1;
            m_LeftSidebar.style.borderRightColor = NotesTheme.BorderSubtle;
            m_RightSidebar.style.borderLeftWidth = 1;
            m_RightSidebar.style.borderLeftColor = NotesTheme.BorderSubtle;
            // split views apply their sizes on the first layout; collapse after that
            rootVisualElement.schedule.Execute(() =>
            {
                SetLeftSidebarCollapsed(EditorPrefs.GetBool(LeftCollapsedPref, false));
                SetRightSidebarCollapsed(EditorPrefs.GetBool(RightCollapsedPref, false));
                // a theme change rebuilds the window: stay in focus mode
                m_FocusMode = false;
                if (SessionState.GetBool(FocusModeSessionKey, false)) SetFocusMode(true, restoring: true);
            });

            // Setup Drag & Drop for Unity Assets onto Notes Window
            SetupDragAndDrop();

            // Quick switcher / command palette overlay (added last so it draws on top)
            m_Palette = new NotePalette();
            rootVisualElement.Add(m_Palette);
            NoteToast.Attach(rootVisualElement);
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

            BindRightSidebar();
        }

        private VisualElement BuildRibbon()
        {
            var ribbon = new VisualElement
            {
                style =
                {
                    width = 42, flexShrink = 0, alignItems = Align.Center,
                    paddingTop = 6, paddingBottom = 6,
                    backgroundColor = NotesTheme.BgSecondary,
                    borderRightWidth = 1, borderRightColor = NotesTheme.BorderSubtle
                }
            };

            Button Item(string commandId)
            {
                var command = m_Commands.Get(commandId);
                string shortcut = command.Hotkey.HasValue ? Shortcut(command.Hotkey.Value) : "";
                var b = NotesIcons.IconButton(command.Icon, shortcut.Length > 0 ? $"{command.Title} ({shortcut})" : command.Title,
                    () => m_Commands.Execute(commandId, remember: false), 18f);
                b.style.width = 32; b.style.height = 32; b.style.marginBottom = 2;
                ribbon.Add(b);
                return b;
            }

            m_LeftToggle = Item("toggle-left-sidebar");
            ribbon.Add(new VisualElement { style = { height = 1, width = 20, marginTop = 4, marginBottom = 6, backgroundColor = NotesTheme.BorderSubtle } });
            Item("open-note");
            Item("search");
            Item("daily-today");
            Item("open-graph");
            Item("tasks-board");
            Item("new-from-template");
            Item("command-palette");

            ribbon.Add(new VisualElement { style = { flexGrow = 1 } });
            m_RightToggle = Item("toggle-right-sidebar");
            Item("settings");
            return ribbon;
        }

        // =====================================================================
        // Sidebars
        // =====================================================================

        private bool IsLeftCollapsed => m_LeftSidebar == null || m_LeftSidebar.IsCollapsed;
        private bool IsRightCollapsed => m_RightSidebar == null || m_RightSidebar.IsCollapsed;

        private void SetLeftSidebarCollapsed(bool collapsed)
        {
            if (m_OuterSplit == null) return;
            if (collapsed != m_LeftSidebar.IsCollapsed)
            {
                if (collapsed) m_OuterSplit.CollapseChild(0); else m_OuterSplit.UnCollapse();
            }
            m_LeftSidebar.SetCollapsed(collapsed);
            EditorPrefs.SetBool(LeftCollapsedPref, collapsed);
            NotesIcons.SetToggled(m_LeftToggle, !collapsed);
        }

        private void SetRightSidebarCollapsed(bool collapsed)
        {
            if (m_InnerSplit == null) return;
            if (collapsed != m_RightSidebar.IsCollapsed)
            {
                if (collapsed) m_InnerSplit.CollapseChild(1); else m_InnerSplit.UnCollapse();
            }
            m_RightSidebar.SetCollapsed(collapsed);
            EditorPrefs.SetBool(RightCollapsedPref, collapsed);
            NotesIcons.SetToggled(m_RightToggle, !collapsed);
        }

        private void ShowSearch(string query = null)
        {
            ShowLeftPane("search");
            m_Search.FocusSearch(query);
        }

        private void RevealInFiles(NoteItem note)
        {
            ShowLeftPane("files");
            m_ListPanel.Reveal(note);
        }

        private void ShowLeftPane(string id)
        {
            m_LeftSidebar.Show(id);
            if (IsLeftCollapsed) SetLeftSidebarCollapsed(false);
        }

        private void ShowRightPane(string id)
        {
            m_RightSidebar.Show(id);
            if (IsRightCollapsed) SetRightSidebarCollapsed(false);
        }

        /// <summary>Focus mode: only the note text — no ribbon, sidebars, tab strips or pane headers.</summary>
        private void SetFocusMode(bool on, bool restoring = false)
        {
            if (m_FocusMode == on || m_Workspace == null) return;
            m_FocusMode = on;
            SessionState.SetBool(FocusModeSessionKey, on);
            if (on)
            {
                // the sidebar states to return to survive window rebuilds
                if (!restoring)
                {
                    SessionState.SetBool(FocusModeSessionKey + ".Left", IsLeftCollapsed);
                    SessionState.SetBool(FocusModeSessionKey + ".Right", IsRightCollapsed);
                }
                SetLeftSidebarCollapsed(true);
                SetRightSidebarCollapsed(true);
            }
            else
            {
                SetLeftSidebarCollapsed(SessionState.GetBool(FocusModeSessionKey + ".Left", false));
                SetRightSidebarCollapsed(SessionState.GetBool(FocusModeSessionKey + ".Right", false));
            }
            UpdateRibbonVisibility();
            m_Workspace.SetChromeHidden(on);
            ActiveEditor?.FocusEditor();
        }

        private static void TrackWidth(VisualElement sidebar, string pref)
        {
            sidebar.RegisterCallback<GeometryChangedEvent>(e =>
            {
                if (e.newRect.width >= 120f && sidebar.resolvedStyle.display == DisplayStyle.Flex) EditorPrefs.SetFloat(pref, e.newRect.width);
            });
        }

        /// <summary>Points the right sidebar at the active pane's note and editor.</summary>
        private void BindRightSidebar()
        {
            var editor = ActiveEditor;
            var note = m_Workspace?.ActiveTabNote;
            if (editor != m_BoundEditor || note != m_BoundNote)
            {
                m_BoundEditor = editor;
                m_BoundNote = note;
                m_Outline?.Bind(editor?.LiveEditor);
            }
            // a pane showing the graph or the board keeps the last note's neighbourhood
            if (note != null) m_LocalGraph?.SetCenter(note);
            m_Backlinks?.Bind(note);
        }

        private void UpdateRibbonVisibility()
        {
            if (m_Ribbon != null) m_Ribbon.style.display = m_FocusMode || !NoteSettings.ShowRibbon ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void OnSettingsChanged()
        {
            // Colors are baked into the elements when they are built: rebuild with the new theme.
            if (m_Workspace == null) return;
            NoteStore.Instance.FlushPendingSaves();
            CreateGUI();
        }

        private void ShowTasksView() => m_Workspace.OpenView(WorkspaceView.TasksKey);

        private void HandleCreateNewNote()
        {
            CreateNoteInTarget("Untitled", "");
            ActiveEditor?.FocusTitle();
        }

        private NoteItem CreateNoteInTarget(string title, string body)
        {
            var note = NoteStore.Instance.CreateNote(title, body, null, m_ListPanel.TargetFolder, m_ListPanel.TargetVault);
            m_Workspace.Open(note);
            ActiveEditor?.FocusEditor();
            return note;
        }

        // =====================================================================
        // Keyboard: quick switcher, command palette, daily note
        // =====================================================================

        private void OnRootKeyDown(KeyDownEvent evt)
        {
            var kb = CodeIDESettings.Current.Keybindings;
            var command = m_Commands.FindByHotkey(action => kb.CheckAction(action, evt));
            if (command != null) command.Run();
            else if (IsKeypadZoom(evt, KeyCode.KeypadPlus)) NoteEditorView.Zoom(1);
            else if (IsKeypadZoom(evt, KeyCode.KeypadMinus)) NoteEditorView.Zoom(-1);
            else return;
            evt.StopPropagation();
        }

        private static bool IsKeypadZoom(KeyDownEvent evt, KeyCode key) => evt.keyCode == key && evt.actionKey && !evt.altKey;

        private static string Shortcut(IDEAction action)
        {
            var binding = CodeIDESettings.Current.Keybindings.Bindings.Find(b => b.Action == action)?.Binding;
            return binding != null ? binding.ToString().Replace("Control", "Ctrl").Replace(", ", "+") : "";
        }

        public void ShowQuickSwitcher() => ShowQuickSwitcher("");

        private void ShowQuickSwitcher(string initialQuery)
        {
            m_Palette.Open("Find or create a note…  (# for headings)", "↑↓ navigate · Enter open · Shift+Enter create · # go to heading · Esc close",
                QuickSwitcherItems, (query, forced) => { if (!query.StartsWith("#")) CreateNoteInTarget(query, ""); }, initialQuery);
        }

        private List<PaletteItem> QuickSwitcherItems(string query)
        {
            if (query.StartsWith("#")) return HeadingItems(query.Substring(1).Trim());

            var store = NoteStore.Instance;
            var recent = store.RecentPaths;
            bool multiVault = store.Vaults.Count > 1;
            var items = new List<(PaletteItem item, DateTime updated)>();

            foreach (var note in store.Notes)
            {
                int recentIndex = -1;
                for (int i = 0; i < recent.Count; i++)
                    if (string.Equals(recent[i], note.FilePath, StringComparison.OrdinalIgnoreCase)) { recentIndex = i; break; }

                var matches = new List<int>();
                int score;
                if (string.IsNullOrWhiteSpace(query)) score = 0;
                else
                {
                    score = FuzzyMatcher.Score(query, note.Title, matches);
                    int pathScore = FuzzyMatcher.Score(query, note.RelativePath ?? "") - 10;
                    int aliasScore = note.Aliases.Count == 0 ? FuzzyMatcher.NoMatch : note.Aliases.Max(a => FuzzyMatcher.Score(query, a)) - 5;
                    if (pathScore > score) { score = pathScore; matches.Clear(); }
                    if (aliasScore > score) { score = aliasScore; matches.Clear(); }
                    if (score == FuzzyMatcher.NoMatch || score < -5) continue;
                }

                if (recentIndex >= 0) score += Math.Max(0, 30 - recentIndex);
                if (note.IsPinned) score += 5;

                var n = note;
                string vaultPrefix = multiVault ? (store.VaultOf(note)?.Name ?? "external") + " · " : "";
                items.Add((new PaletteItem
                {
                    Title = note.Title,
                    Subtitle = vaultPrefix + note.RelativePath,
                    Icon = note.IsPinned ? "star" : "file-text",
                    Hint = recentIndex >= 0 && string.IsNullOrWhiteSpace(query) ? "recent" : null,
                    Score = score,
                    Matches = matches,
                    Preview = () => NotePreviewText(n),
                    OnChoose = () => { m_Workspace.Open(n); ActiveEditor?.FocusEditor(); }
                }, note.UpdatedAt));
            }

            return items.OrderByDescending(i => i.item.Score)
                        .ThenByDescending(i => i.updated)
                        .Select(i => i.item)
                        .ToList();
        }

        private static string NotePreviewText(NoteItem note)
        {
            var lines = MarkdownScan.NormalizeNewlines(note.Body ?? "").Split('\n').Take(30);
            string text = string.Join("\n", lines).Trim();
            return text.Length > 0 ? text : "(empty note)";
        }

        /// <summary>"Go to heading": the headings of the active note, fuzzy matched.</summary>
        private List<PaletteItem> HeadingItems(string query)
        {
            var editor = ActiveEditor;
            var items = new List<PaletteItem>();
            if (editor?.CurrentNote == null) return items;

            foreach (var heading in editor.LiveEditor.GetHeadings())
            {
                var matches = new List<int>();
                int score = string.IsNullOrEmpty(query) ? 0 : FuzzyMatcher.Score(query, heading.Text, matches);
                if (score == FuzzyMatcher.NoMatch) continue;
                int line = heading.Line;
                items.Add(new PaletteItem
                {
                    Title = new string(' ', (heading.Level - 1) * 3) + heading.Text,
                    Icon = heading.Level <= 3 ? "heading-" + heading.Level : "hash",
                    Hint = "H" + heading.Level,
                    Score = score,
                    Matches = matches.Select(i => i + (heading.Level - 1) * 3).ToList(),
                    OnChoose = () => { editor.RevealBodyLine(line); editor.FocusEditor(); }
                });
            }
            return string.IsNullOrEmpty(query) ? items : items.OrderByDescending(i => i.Score).ToList();
        }

        public void ShowCommandPalette()
        {
            m_Palette.Open("Type a command…", "↑↓ navigate · Enter run · Esc close", CommandItems);
        }

        private List<PaletteItem> CommandItems(string query)
        {
            PaletteItem ToItem(NoteCommand c, string section, int score, List<int> matches) => new PaletteItem
            {
                Title = c.Title,
                Icon = c.Icon,
                Section = section,
                Hint = section == null ? c.Category : null,
                Shortcut = c.Hotkey.HasValue ? Shortcut(c.Hotkey.Value) : null,
                Score = score,
                Matches = matches,
                OnChoose = () => m_Commands.Execute(c.Id)
            };

            var available = m_Commands.Available().ToList();
            if (string.IsNullOrWhiteSpace(query))
            {
                var items = new List<PaletteItem>();
                var recent = m_Commands.RecentAvailable().ToList();
                items.AddRange(recent.Select(c => ToItem(c, "Recently used", 0, null)));
                foreach (var group in available.GroupBy(c => c.Category))
                    items.AddRange(group.Select(c => ToItem(c, group.Key, 0, null)));
                return items;
            }

            var ranked = new List<PaletteItem>();
            foreach (var c in available)
            {
                var matches = new List<int>();
                int score = FuzzyMatcher.Score(query, c.Title, matches);
                if (score == FuzzyMatcher.NoMatch) continue;
                int recentIndex = m_Commands.Recent.ToList().IndexOf(c.Id);
                if (recentIndex >= 0) score += NoteCommandRegistry.MaxRecent - recentIndex;
                ranked.Add(ToItem(c, null, score, matches));
            }
            return ranked.OrderByDescending(i => i.Score).ToList();
        }

        /// <summary>Every Fuzzy Notes command. Availability is checked when the palette opens or a hotkey is pressed.</summary>
        private NoteCommandRegistry BuildCommandRegistry()
        {
            var r = new NoteCommandRegistry();
            r.SetRecent(EditorPrefs.GetString(RecentCommandsPref, "").Split('\n'));
            r.RecentChanged += () => EditorPrefs.SetString(RecentCommandsPref, string.Join("\n", r.Recent));

            var store = NoteStore.Instance;
            NoteItem Current() => ActiveEditor?.CurrentNote;
            bool HasNote() => Current() != null;
            bool HasVaultNote() => Current() != null && !Current().IsExternal;
            void Later(Action action) => EditorApplication.delayCall += () => action();

            const string notes = "Notes", search = "Search", editor = "Editor", view = "Appearance", workspace = "Workspace", ide = "Unity & code";

            // Notes
            r.Add("open-note", "Open note…", "file-text", notes, () => Later(ShowQuickSwitcher), IDEAction.NoteQuickSwitcher);
            r.Add("new-note", "Create new note", "file-plus", notes, HandleCreateNewNote);
            r.Add("new-from-template", "Create note from template…", "layout-template", notes, () => Later(() => ShowTemplatePicker(asNewNote: true)));
            r.Add("daily-today", "Open today's daily note", "calendar", notes, OpenDailyNote, IDEAction.NoteDailyNote);
            r.Add("daily-yesterday", "Open yesterday's daily note", "calendar-days", notes, () => OpenDailyNote(DateTime.Today.AddDays(-1)));
            r.Add("tasks-board", "Open tasks board", "square-kanban", notes, ShowTasksView);
            r.Add("open-graph", "Open graph view", "waypoints", notes, () => m_Workspace.OpenView(WorkspaceView.GraphKey), IDEAction.NoteOpenGraph);
            r.Add("open-local-graph", "Open local graph", "waypoints", notes, () => ShowRightPane("localgraph"), null, HasNote);
            r.Add("go-to-heading", "Go to heading…", "hash", notes, () => Later(() => ShowQuickSwitcher("#")), null, HasNote);
            r.Add("toggle-bookmark", "Toggle bookmark", "bookmark", notes, () => store.TogglePin(Current()), null, HasNote);
            r.Add("move-note", "Move note to…", "folder", notes, () => Later(() => ShowMovePicker(new[] { Current() })), null, HasVaultNote);
            r.Add("copy-link", "Copy link to note", "link", notes, () =>
            {
                EditorGUIUtility.systemCopyBuffer = $"[[{Current().LinkName}]]";
                NoteToast.Show("Link copied", NoteToastKind.Success);
            }, null, HasNote);
            r.Add("reveal-in-files", "Reveal note in Files", "locate-fixed", notes, () => RevealInFiles(Current()), null, HasVaultNote);
            r.Add("reveal-in-os", "Reveal note in File Explorer", "external-link", notes, () => { if (File.Exists(Current().FilePath)) EditorUtility.RevealInFinder(Current().FilePath); }, null, HasNote);
            r.Add("delete-note", "Delete current note", "trash-2", notes, () => NoteActions.Delete(Current()), null, HasNote);
            r.Add("undo-delete", "Undo last delete", "undo-2", notes, () =>
            {
                var restored = store.RestoreLastDeleted();
                NoteToast.Show(restored != null ? $"Restored '{restored.Title}'" : "Nothing to restore", restored != null ? NoteToastKind.Success : NoteToastKind.Info);
            });
            r.Add("reload", "Reload notes from disk", "refresh-cw", notes, store.CheckExternalChanges);

            // Search
            r.Add("search", "Search in all notes", "search", search, () => ShowSearch(), IDEAction.NoteSearch);
            r.Add("search-tasks", "Search open tasks", "list-todo", search, () => ShowSearch("task:todo"));
            r.Add("find-in-note", "Find in current note", "search", search, () => ActiveEditor.OpenFind(), IDEAction.NoteFind, HasNote).LocalHotkey = true;
            r.Add("replace-in-note", "Find and replace in current note", "search", search, () => ActiveEditor.OpenFind(withReplace: true), null, HasNote);

            // Editor
            r.Add("toggle-reading", "Toggle reading view", "book-open", editor, () => ActiveEditor.ToggleReadingMode(), IDEAction.NoteToggleReadingMode, HasNote);
            r.Add("insert-template", "Insert template…", "layout-template", editor, () => Later(() => ShowTemplatePicker(asNewNote: false)), null, HasNote);
            r.Add("fold-all", "Fold all headings", "chevrons-down-up", editor, () => ActiveEditor.SetAllFolded(true), null, HasNote);
            r.Add("unfold-all", "Unfold all headings", "arrow-up-down", editor, () => ActiveEditor.SetAllFolded(false), null, HasNote);

            // Appearance
            r.Add("zoom-in", "Zoom in", "plus", view, () => NoteEditorView.Zoom(1), IDEAction.NoteZoomIn);
            r.Add("zoom-out", "Zoom out", "minus", view, () => NoteEditorView.Zoom(-1), IDEAction.NoteZoomOut);
            r.Add("zoom-reset", "Reset zoom", "rotate-ccw", view, () => NoteEditorView.Zoom(0), IDEAction.NoteZoomReset);
            r.Add("toggle-readable-width", "Toggle readable line length", "text", view, () => NoteSettings.ReadableLineLength = !NoteSettings.ReadableLineLength);
            r.Add("toggle-formatting-bar", "Toggle formatting toolbar", "bold", view, () => NoteSettings.ShowFormattingBar = !NoteSettings.ShowFormattingBar);
            r.Add("toggle-focus-mode", "Toggle focus mode", "maximize-2", view, () => SetFocusMode(!m_FocusMode), IDEAction.NoteToggleFocusMode);

            // Workspace
            r.Add("command-palette", "Open command palette", "command", workspace, () => Later(ShowCommandPalette), IDEAction.NoteCommandPalette);
            r.Add("toggle-left-sidebar", "Toggle left sidebar", "panel-left", workspace, () => SetLeftSidebarCollapsed(!IsLeftCollapsed), IDEAction.NoteToggleLeftSidebar);
            r.Add("toggle-right-sidebar", "Toggle right sidebar", "panel-right", workspace, () => SetRightSidebarCollapsed(!IsRightCollapsed), IDEAction.NoteToggleRightSidebar);
            r.Add("show-files", "Show files", "folder", workspace, () => ShowLeftPane("files"));
            r.Add("show-bookmarks", "Show bookmarks", "bookmark", workspace, () => ShowLeftPane("bookmarks"));
            r.Add("show-tags", "Show tags", "tags", workspace, () => ShowLeftPane("tags"));
            r.Add("show-outline", "Show outline", "list", workspace, () => ShowRightPane("outline"));
            r.Add("show-backlinks", "Show backlinks", "link", workspace, () => ShowRightPane("backlinks"));
            r.Add("navigate-back", "Navigate back", "arrow-left", workspace, () => m_Workspace.NavigateActive(-1), IDEAction.NoteNavigateBack);
            r.Add("navigate-forward", "Navigate forward", "arrow-right", workspace, () => m_Workspace.NavigateActive(1), IDEAction.NoteNavigateForward);
            r.Add("open-right", "Open current note to the right", "columns-2", workspace, () => m_Workspace.Open(Current(), NoteOpenMode.SplitRight), null, HasNote);
            r.Add("split-right", "Split right", "columns-2", workspace, () => m_Workspace.SplitActive(DropZone.Right), IDEAction.NoteSplitRight);
            r.Add("split-down", "Split down", "rows-2", workspace, () => m_Workspace.SplitActive(DropZone.Bottom), IDEAction.NoteSplitDown);
            r.Add("close-tab", "Close current tab", "x", workspace, () => m_Workspace.CloseActiveTab(), IDEAction.NoteCloseTab);
            r.Add("close-pane", "Close current pane", "x", workspace, () => m_Workspace.ClosePane(m_Workspace.ActiveLeaf), null, () => !m_Workspace.Layout.IsOnlyLeaf(m_Workspace.ActiveLeaf));
            r.Add("next-tab", "Go to next tab", "arrow-right", workspace, () => m_Workspace.CycleTab(1), IDEAction.NoteNextTab);
            r.Add("previous-tab", "Go to previous tab", "arrow-left", workspace, () => m_Workspace.CycleTab(-1), IDEAction.NotePreviousTab);
            r.Add("focus-next-pane", "Focus next pane", "arrow-right", workspace, () => m_Workspace.FocusNextPane(), IDEAction.NoteFocusNextPane);
            r.Add("settings", "Open Fuzzy Notes settings", "settings", workspace, NoteSettingsWindow.Open);

            // Unity & code
            r.Add("insert-commits", "Insert today's commits", "git-commit-horizontal", ide, InsertTodaysCommits, null, HasNote);
            r.Add("insert-unity-links", "Insert links to selected Unity objects", "box", ide, InsertSelectedObjectLinks, null, () => HasNote() && Selection.objects.Length > 0);
            r.Add("code-todo", "Go to code TODO…", "code", ide, () => Later(ShowCodeTodoPicker));
            r.Add("collect-todos", "Collect code TODOs into a note", "list-todo", ide, GitNoteBridge.ScanAndCollectProjectTodos);
            return r;
        }

        // =====================================================================
        // IDE / Unity integration commands
        // =====================================================================

        private void ShowCodeTodoPicker()
        {
            var todos = CodeTodoIndex.All;
            string footer = CodeTodoIndex.IsReady ? $"{todos.Count} TODO comments · Enter open in IDE · Esc close" : "Scanning scripts… reopen in a moment · Esc close";
            m_Palette.Open("Find a code TODO…", footer, query =>
            {
                var items = new List<PaletteItem>();
                foreach (var todo in todos)
                {
                    var matches = new List<int>();
                    string title = $"{todo.Kind}: {todo.Text}";
                    int score = string.IsNullOrWhiteSpace(query) ? 0 : Math.Max(FuzzyMatcher.Score(query, title, matches), FuzzyMatcher.Score(query, todo.FileName) - 5);
                    if (score == FuzzyMatcher.NoMatch) continue;
                    var t = todo;
                    items.Add(new PaletteItem
                    {
                        Icon = todo.Kind == "TODO" ? "square-check" : "triangle-alert",
                        Title = title,
                        Subtitle = todo.AssetPath + ":" + (todo.Line + 1),
                        Score = score,
                        Matches = matches,
                        OnChoose = () => NoteLinkResolver.OpenCodeFile(t.AssetPath, t.Line + 1)
                    });
                }
                return string.IsNullOrWhiteSpace(query) ? items : items.OrderByDescending(i => i.Score).ToList();
            });
        }

        private async void InsertTodaysCommits()
        {
            var editor = ActiveEditor;
            var note = editor?.CurrentNote;
            if (note == null) return;
            string list = await GitLinks.GetTodaysCommitsMarkdownAsync();
            if (this == null || editor.CurrentNote != note) return;
            if (string.IsNullOrEmpty(list))
            {
                NoteToast.Show("No commits today");
                return;
            }
            editor.InsertAtCaret("## Today's commits\n" + list);
        }

        private void InsertSelectedObjectLinks()
        {
            var links = Selection.objects.Select(UnityObjectLinks.CreateLink).Where(l => l != null).ToList();
            if (links.Count > 0) ActiveEditor?.InsertTextAtCursor(string.Join(" ", links) + " ", "");
        }

        // =====================================================================
        // Daily notes, templates, move
        // =====================================================================

        private void OpenDailyNote() => OpenDailyNote(DateTime.Today);

        private void OpenDailyNote(DateTime date)
        {
            var note = NoteStore.Instance.OpenDailyNote(date, NoteSettings.DailyOptions, NoteTemplateContextFactory.Create());
            if (note != null) m_Workspace.Open(note);
            ActiveEditor?.FocusEditor();
        }

        private void ShowTemplatePicker(bool asNewNote)
        {
            var templates = NoteStore.Instance.GetTemplates(NoteSettings.TemplatesFolder);
            if (templates.Count == 0)
            {
                NoteToast.Show($"No templates yet. Put notes in the '{NoteSettings.TemplatesFolder}' folder of the project vault — they can use " +
                    "{{date}}, {{time}}, {{title}}, {{branch}}, {{scene}} and {{selection}}.", NoteToastKind.Info, "Settings", NoteSettingsWindow.Open, 8000);
                return;
            }

            m_Palette.Open(asNewNote ? "New note from template…" : "Insert template…", "Enter choose · Esc close", query =>
            {
                var items = new List<PaletteItem>();
                foreach (var t in templates)
                {
                    var matches = new List<int>();
                    int score = string.IsNullOrWhiteSpace(query) ? 0 : FuzzyMatcher.Score(query, t.Title, matches);
                    if (score == FuzzyMatcher.NoMatch) continue;
                    var template = t;
                    items.Add(new PaletteItem
                    {
                        Title = t.Title,
                        Icon = "layout-template",
                        Subtitle = t.RelativePath,
                        Score = score,
                        Matches = matches,
                        OnChoose = () => ApplyTemplate(template, asNewNote)
                    });
                }
                return items.OrderByDescending(i => i.Score).ToList();
            });
        }

        private void ApplyTemplate(NoteItem template, bool asNewNote)
        {
            if (asNewNote)
            {
                m_Palette.OpenPrompt($"Title of the new note from '{template.Title}'…", null, name =>
                {
                    var ctx = NoteTemplateContextFactory.Create(name);
                    CreateNoteInTarget(name, NoteTemplates.Apply(template.Body, ctx));
                    ActiveEditor?.FocusEditor();
                });
            }
            else
            {
                var editor = ActiveEditor;
                if (editor == null) return;
                var ctx = NoteTemplateContextFactory.Create(editor.CurrentNote?.Title ?? "", editor.SelectedText);
                editor.InsertAtCaret(NoteTemplates.Apply(template.Body, ctx));
            }
        }

        private void ShowMovePicker(IReadOnlyList<NoteItem> notes)
        {
            var store = NoteStore.Instance;
            notes = notes?.Where(n => n != null && !n.IsExternal && store.VaultOf(n)?.IsProjectFiles != true).ToList();
            if (notes == null || notes.Count == 0) return;
            var note = notes[0];
            var targets = new List<(NoteVault vault, string folder)>();
            foreach (var vault in store.Vaults)
            {
                if (vault.IsProjectFiles) continue; // notes never move into Assets
                targets.Add((vault, ""));
                foreach (var f in store.GetFolders(vault)) targets.Add((vault, f));
            }

            bool multiVault = store.Vaults.Count > 1;
            string what = notes.Count == 1 ? $"'{note.Title}'" : $"{notes.Count} notes";
            m_Palette.Open($"Move {what} to…", "Enter move · Shift+Enter create folder · Esc close", query =>
            {
                var items = new List<PaletteItem>();
                foreach (var (vault, folder) in targets)
                {
                    string label = folder.Length == 0 ? "/ (vault root)" : folder;
                    if (multiVault) label = vault.Name + " / " + (folder.Length == 0 ? "" : folder);
                    var matches = new List<int>();
                    int score = string.IsNullOrWhiteSpace(query) ? 0 : FuzzyMatcher.Score(query, label, matches);
                    if (score == FuzzyMatcher.NoMatch) continue;
                    var v = vault;
                    var f = folder;
                    items.Add(new PaletteItem
                    {
                        Title = label,
                        Icon = "folder",
                        Score = score,
                        Matches = matches,
                        Hint = notes.Count == 1 && v == store.VaultOf(note) && f == note.Folder ? "current" : null,
                        OnChoose = () => MoveNotes(notes, v, f)
                    });
                }
                return items.OrderByDescending(i => i.Score).ToList();
            }, (query, forced) =>
            {
                var vault = store.VaultOf(note) ?? store.PrimaryVault;
                if (store.CreateFolder(vault, query)) MoveNotes(notes, vault, query.Replace('\\', '/').Trim('/'));
            });
        }

        private static void MoveNotes(IReadOnlyList<NoteItem> notes, NoteVault vault, string folder)
        {
            var failed = notes.Where(n => !NoteStore.Instance.MoveNote(n, vault, folder)).Select(n => n.Title).ToList();
            int moved = notes.Count - failed.Count;
            if (failed.Count > 0)
                NoteToast.Show($"Could not move {string.Join(", ", failed.Select(n => $"'{n}'"))}. A note with the same name may already exist there.", NoteToastKind.Error);
            else if (moved > 0)
                NoteToast.Show(moved == 1 ? $"Moved to {(folder.Length > 0 ? folder : vault.Name)}" : $"Moved {moved} notes to {(folder.Length > 0 ? folder : vault.Name)}", NoteToastKind.Success);
        }

        private void HandleOpenExternalFile()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = EditorUtility.OpenFilePanel("Open Markdown File", projectRoot, "md");
            if (!string.IsNullOrEmpty(path))
            {
                var note = NoteStore.Instance.OpenExternalFile(path);
                if (note != null) m_Workspace.Open(note);
            }
        }

        private void HandleExecuteCommand(string command)
        {
            TerminalNoteBridge.RunCommand(command);
        }

        private void SetupDragAndDrop()
        {
            // TrickleDown: handled at the root before any child element can swallow or reject the drag.
            rootVisualElement.RegisterCallback<DragUpdatedEvent>(OnRootDragUpdated, TrickleDown.TrickleDown);
            rootVisualElement.RegisterCallback<DragPerformEvent>(OnRootDragPerform, TrickleDown.TrickleDown);
        }

        private void OnRootDragUpdated(DragUpdatedEvent evt)
        {
            if (IsInternalNoteDrag() || !HasDroppableContent()) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            evt.StopPropagation();
        }

        private void OnRootDragPerform(DragPerformEvent evt)
        {
            if (IsInternalNoteDrag() || !HasDroppableContent()) return;
            DragAndDrop.AcceptDrag();
            evt.StopPropagation();
            HandleDrop();
        }

        /// <summary>Notes and folders dragged inside the list panel are handled by the list itself.</summary>
        private static bool IsInternalNoteDrag() =>
            DragAndDrop.GetGenericData(FileExplorerView.DragNoteKey) != null || DragAndDrop.GetGenericData(FileExplorerView.DragFolderKey) != null ||
            DragAndDrop.GetGenericData(NoteTabsBar.DragTabKey) != null;

        private static bool HasDroppableContent() =>
            (DragAndDrop.objectReferences != null && DragAndDrop.objectReferences.Any(o => o != null)) ||
            (DragAndDrop.paths != null && DragAndDrop.paths.Any(p => !string.IsNullOrEmpty(p)));

        private void HandleDrop()
        {
            var editor = ActiveEditor;
            if (editor == null || editor.CurrentNote == null)
            {
                NoteToast.Show("Open a note first", NoteToastKind.Warning);
                return;
            }

            // Hierarchy drags carry objects (and sometimes empty paths); Project drags carry both; Explorer drags only paths.
            var handledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var links = new List<string>();
            bool unsavedScene = false;

            foreach (var obj in DragAndDrop.objectReferences ?? Array.Empty<UnityEngine.Object>())
            {
                if (obj == null) continue;
                string assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    handledPaths.Add(assetPath);
                    DropPath(assetPath, links);
                    continue;
                }

                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go != null && string.IsNullOrEmpty(go.scene.path)) { unsavedScene = true; continue; }
                string link = UnityObjectLinks.CreateLink(obj);
                if (link != null) links.Add(link);
            }

            foreach (var path in DragAndDrop.paths ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(path) || !handledPaths.Add(path)) continue;
                DropPath(path, links);
            }

            if (links.Count > 0) editor.InsertTextAtCursor(string.Join(" ", links) + " ", "");
            if (unsavedScene) NoteToast.Show("Save the scene first — objects in an unsaved scene can't be linked", NoteToastKind.Warning);
        }

        /// <summary>Opens .md files, embeds images and collects links for everything else.</summary>
        private void DropPath(string path, List<string> links)
        {
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                NoteStore.Instance.OpenExternalFile(Path.GetFullPath(path));
                return;
            }

            // Images are embedded: project images by path, outside files are copied into the vault
            if (NoteImageCache.IsImageFile(path))
            {
                bool inProject = NoteRefLinks.IsProjectPath(path);
                string embed = inProject ? $"![]({path.Replace(" ", "%20")})" : null;
                if (!inProject)
                {
                    string imported = NoteStore.Instance.ImportAttachment(Path.GetFullPath(path));
                    if (imported != null) embed = $"![[{imported}]]";
                }
                if (embed != null) ActiveEditor?.InsertBlock(embed);
                return;
            }

            // Scripts by file name, other assets by GUID (survives moves/renames)
            var asset = NoteRefLinks.IsProjectPath(path) ? AssetDatabase.LoadMainAssetAtPath(path) : null;
            string link = asset != null ? UnityObjectLinks.CreateLink(asset) : null;
            if (link == null && NoteRefLinks.IsProjectPath(path)) link = $"[[asset:{path}]]";
            if (link != null) links.Add(link);
        }
    }
}
