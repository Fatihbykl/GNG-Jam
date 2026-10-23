using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// One workspace pane: its tab strip and the view of the active tab (a note editor, the tasks board or an
    /// empty state). Notes and tabs dragged over the content show where they would land (center = this pane,
    /// edges = split).
    /// </summary>
    public class LeafView : VisualElement
    {
        private const float EdgeFraction = 0.25f;

        public readonly WorkspaceLeaf Leaf;
        private readonly WorkspaceView m_Workspace;
        private readonly NoteTabsBar m_TabsBar;
        private readonly VisualElement m_Content;
        private readonly VisualElement m_DropOverlay;
        private NoteEditorView m_Editor;
        private TasksKanbanView m_Kanban;
        private GraphView m_Graph;
        private VisualElement m_Empty;
        private VisualElement m_RecentList;
        private bool m_ChromeHidden;

        /// <summary>The note editor when the active tab is a note, otherwise null.</summary>
        public NoteEditorView ActiveEditor => Leaf.ActiveTab?.Ref is NoteItem && m_Editor != null ? m_Editor : null;

        public LeafView(WorkspaceView workspace, WorkspaceLeaf leaf)
        {
            m_Workspace = workspace;
            Leaf = leaf;
            style.flexGrow = 1;
            style.minWidth = 120;
            style.minHeight = 80;

            m_TabsBar = new NoteTabsBar(leaf) { Describe = WorkspaceView.DescribeTab };
            m_TabsBar.OnTabSelected += i => m_Workspace.SelectTab(Leaf, i);
            m_TabsBar.OnTabClosed += i => m_Workspace.CloseTab(Leaf, i);
            m_TabsBar.OnTabContextClick += i => m_Workspace.ShowTabMenu(Leaf, i);
            m_TabsBar.OnNewTabClicked += () => m_Workspace.RequestQuickSwitcher(Leaf);
            m_TabsBar.OnDropped += (payload, index) => m_Workspace.DropOnTabStrip(Leaf, payload, index);
            Add(m_TabsBar);

            m_Content = new VisualElement { style = { flexGrow = 1, position = Position.Relative, minHeight = 0 } };
            Add(m_Content);

            m_DropOverlay = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute, display = DisplayStyle.None,
                    backgroundColor = NotesTheme.AccentSoft,
                    borderTopWidth = 2, borderBottomWidth = 2, borderLeftWidth = 2, borderRightWidth = 2,
                    borderTopColor = NotesTheme.Accent, borderBottomColor = NotesTheme.Accent,
                    borderLeftColor = NotesTheme.Accent, borderRightColor = NotesTheme.Accent,
                    borderTopLeftRadius = 6, borderTopRightRadius = 6, borderBottomLeftRadius = 6, borderBottomRightRadius = 6
                }
            };

            // Any press or focus inside the pane makes it the active one.
            RegisterCallback<PointerDownEvent>(_ => m_Workspace.Activate(Leaf), TrickleDown.TrickleDown);
            RegisterCallback<FocusInEvent>(_ => m_Workspace.Activate(Leaf));
            // mouse back / forward buttons
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 3 && e.button != 4) return;
                m_Workspace.Navigate(Leaf, e.button == 3 ? -1 : 1);
                e.StopPropagation();
            });

            m_Content.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            m_Content.RegisterCallback<DragPerformEvent>(OnDragPerform);
            m_Content.RegisterCallback<DragLeaveEvent>(_ => HideOverlay());
            m_Content.RegisterCallback<DragExitedEvent>(_ => HideOverlay());
        }

        /// <summary>Re-renders the tab strip and shows the active tab's view.</summary>
        public void Refresh(bool focused)
        {
            m_TabsBar.SetTabs(Leaf.Tabs, Leaf.ActiveIndex, focused);

            var tab = Leaf.ActiveTab;
            if (tab?.Ref is NoteItem note)
            {
                var editor = EnsureEditor();
                if (editor.CurrentNote != note) editor.SetNote(note);
                editor.SetNavigationState(Leaf.History.CanGoBack, Leaf.History.CanGoForward);
                Show(editor);
            }
            else if (tab != null && tab.Key == WorkspaceView.GraphKey)
            {
                if (m_Graph == null)
                {
                    m_Graph = new GraphView(local: false);
                    m_Graph.OnOpenNote += (note, mode) => { m_Workspace.Activate(Leaf); m_Workspace.Open(note, mode); };
                    m_Graph.OnSearchRequested += m_Workspace.RaiseSearch;
                    m_Content.Add(m_Graph);
                }
                Show(m_Graph);
            }
            else if (tab != null && tab.Key == WorkspaceView.TasksKey)
            {
                if (m_Kanban == null)
                {
                    m_Kanban = new TasksKanbanView();
                    m_Kanban.Prompt = (placeholder, onSubmit) => m_Workspace.Prompt?.Invoke(placeholder, onSubmit);
                    m_Kanban.OnBackToNotesClicked += () => m_Workspace.CloseTab(Leaf, Leaf.IndexOf(t => t.Key == WorkspaceView.TasksKey));
                    m_Content.Add(m_Kanban);
                }
                Show(m_Kanban);
            }
            else
            {
                Show(EnsureEmpty());
                RefreshRecentNotes();
            }
        }

        public void SetFocused(bool focused) => m_TabsBar.SetFocused(focused);

        /// <summary>Focus mode: no tab strip and no editor header.</summary>
        public void SetChromeHidden(bool hidden)
        {
            m_ChromeHidden = hidden;
            m_TabsBar.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;
            m_Editor?.SetHeaderHidden(hidden);
        }

        /// <summary>The note editor of this pane, creating it on first use.</summary>
        public NoteEditorView EnsureEditor()
        {
            if (m_Editor != null) return m_Editor;
            m_Editor = new NoteEditorView();
            m_Editor.OnExecuteCommand += m_Workspace.RaiseExecuteCommand;
            m_Editor.OnShowSidebarPane += m_Workspace.RaiseShowSidebarPane;
            m_Editor.NoteLoaded += _ => m_Workspace.OnLeafContentChanged(this);
            m_Editor.OnNavigate += direction => m_Workspace.Navigate(Leaf, direction);
            m_Editor.OnOpenNote += (note, mode, subpath) =>
            {
                m_Workspace.Activate(Leaf);
                m_Workspace.Open(note, mode);
                // the new pane lays out its editor on the next frame
                if (!string.IsNullOrEmpty(subpath)) m_Workspace.schedule.Execute(() => m_Workspace.ActiveEditor?.RevealSubpath(subpath));
            };
            m_Editor.OnRevealInExplorer += m_Workspace.RaiseRevealInExplorer;
            m_Editor.OnInsertTemplate += m_Workspace.RaiseInsertTemplate;
            m_Editor.SetHeaderHidden(m_ChromeHidden);
            if (NoteSettings.DefaultReadingMode) m_Editor.SetReadingMode(true, focus: false);
            m_Content.Add(m_Editor);
            return m_Editor;
        }

        private void Show(VisualElement view)
        {
            foreach (var child in m_Content.Children())
                if (child != m_DropOverlay) child.style.display = child == view ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private VisualElement EnsureEmpty()
        {
            if (m_Empty != null) return m_Empty;

            m_Empty = new VisualElement { style = { flexGrow = 1, alignItems = Align.Center, justifyContent = Justify.Center, backgroundColor = NotesTheme.BgPrimary } };
            var column = new VisualElement { style = { alignItems = Align.FlexStart } };
            column.Add(new Label("No note is open") { style = { fontSize = 18, unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextMuted, marginBottom = 12 } });

            void Action(string text, string shortcut, System.Action onClick)
            {
                var link = new Label(shortcut != null ? $"{text}  ({shortcut})" : text) { style = { fontSize = 13, color = NotesTheme.Accent, marginBottom = 6, paddingLeft = 0 } };
                link.RegisterCallback<ClickEvent>(_ => onClick());
                link.RegisterCallback<PointerEnterEvent>(_ => link.style.color = NotesTheme.TextNormal);
                link.RegisterCallback<PointerLeaveEvent>(_ => link.style.color = NotesTheme.Accent);
                column.Add(link);
            }

            Action("Create a new note", null, () => m_Workspace.RaiseEmptyStateAction(WorkspaceView.EmptyStateAction.NewNote));
            Action("Go to a note", WorkspaceView.ShortcutText(IDEAction.NoteQuickSwitcher), () => m_Workspace.RequestQuickSwitcher(Leaf));
            Action("Open today's daily note", WorkspaceView.ShortcutText(IDEAction.NoteDailyNote), () => m_Workspace.RaiseEmptyStateAction(WorkspaceView.EmptyStateAction.DailyNote));
            if (!m_Workspace.Layout.IsOnlyLeaf(Leaf))
                Action("Close this pane", null, () => m_Workspace.ClosePane(Leaf));

            m_RecentList = new VisualElement { style = { marginTop = 14 } };
            column.Add(m_RecentList);

            m_Empty.Add(column);
            m_Content.Add(m_Empty);
            return m_Empty;
        }

        private void RefreshRecentNotes()
        {
            m_RecentList.Clear();
            var store = NoteStore.Instance;
            int shown = 0;
            foreach (var path in store.RecentPaths)
            {
                var note = store.FindByPath(path);
                if (note == null) continue;
                if (shown++ == 0)
                    m_RecentList.Add(new Label("Recent notes") { style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextFaint, marginBottom = 4 } });

                var row = new VisualElement { tooltip = note.RelativePath ?? note.FilePath, style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, height = 22 } };
                row.Add(NotesIcons.Create("file-text", 14f, NotesTheme.TextFaint));
                var label = new Label(note.Title) { style = { fontSize = 12, marginLeft = 6, color = NotesTheme.TextMuted } };
                row.Add(label);
                row.RegisterCallback<ClickEvent>(e =>
                {
                    m_Workspace.Activate(Leaf);
                    m_Workspace.Open(note, e.altKey ? NoteOpenMode.SplitRight : NoteOpenMode.Default);
                });
                row.RegisterCallback<PointerEnterEvent>(_ => label.style.color = NotesTheme.TextNormal);
                row.RegisterCallback<PointerLeaveEvent>(_ => label.style.color = NotesTheme.TextMuted);
                m_RecentList.Add(row);
                if (shown == 5) break;
            }
        }

        // =====================================================================
        // Drop zones
        // =====================================================================

        private DropZone ZoneAt(Vector2 worldPos)
        {
            var rect = m_Content.worldBound;
            float fx = (worldPos.x - rect.x) / Mathf.Max(1f, rect.width);
            float fy = (worldPos.y - rect.y) / Mathf.Max(1f, rect.height);

            // nearest edge within the edge band wins
            float left = fx, right = 1f - fx, top = fy, bottom = 1f - fy;
            float min = Mathf.Min(Mathf.Min(left, right), Mathf.Min(top, bottom));
            if (min > EdgeFraction) return DropZone.Center;
            if (min == left) return DropZone.Left;
            if (min == right) return DropZone.Right;
            return min == top ? DropZone.Top : DropZone.Bottom;
        }

        private void OnDragUpdated(DragUpdatedEvent e)
        {
            var payload = NoteTabsBar.CurrentPayload();
            if (payload == null) return;
            var zone = ZoneAt(e.mousePosition);
            // a pane cannot be split off its own only tab
            if (payload is TabDragData d && d.Leaf == Leaf && Leaf.Tabs.Count <= 1) zone = DropZone.Center;

            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            ShowOverlay(zone);
            e.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent e)
        {
            var payload = NoteTabsBar.CurrentPayload();
            HideOverlay();
            if (payload == null) return;
            DragAndDrop.AcceptDrag();
            var zone = ZoneAt(e.mousePosition);
            NoteTabsBar.ClearPayload();
            e.StopPropagation();
            m_Workspace.DropOnPane(Leaf, payload, zone);
        }

        private void ShowOverlay(DropZone zone)
        {
            if (m_DropOverlay.parent != m_Content) m_Content.Add(m_DropOverlay);
            m_DropOverlay.BringToFront();
            var s = m_DropOverlay.style;
            s.display = DisplayStyle.Flex;
            s.left = Length.Percent(zone == DropZone.Right ? 50 : 0);
            s.top = Length.Percent(zone == DropZone.Bottom ? 50 : 0);
            s.width = Length.Percent(zone == DropZone.Left || zone == DropZone.Right ? 50 : 100);
            s.height = Length.Percent(zone == DropZone.Top || zone == DropZone.Bottom ? 50 : 100);
        }

        private void HideOverlay() => m_DropOverlay.style.display = DisplayStyle.None;
    }
}
