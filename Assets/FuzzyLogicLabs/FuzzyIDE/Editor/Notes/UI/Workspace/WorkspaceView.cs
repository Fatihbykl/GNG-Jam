using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    public enum NoteOpenMode { Default, SplitRight, SplitDown }

    /// <summary>
    /// The central editing area: renders a <see cref="WorkspaceLayout"/> as nested split views of
    /// <see cref="LeafView"/> panes and keeps it in sync with the note store. The active pane's note is
    /// <see cref="NoteStore.ActiveNote"/>, so everything that calls <c>SetActiveNote</c> opens notes here.
    /// The layout is saved per project under Library/.
    /// </summary>
    public class WorkspaceView : VisualElement
    {
        public const string NotePrefix = "note:";
        public const string TasksKey = "view:tasks";
        public const string GraphKey = "view:graph";
        private const string LegacyTabsSessionKey = "FuzzyNotes.OpenTabs";

        public enum EmptyStateAction { NewNote, DailyNote }

        private readonly Dictionary<WorkspaceLeaf, LeafView> m_LeafViews = new Dictionary<WorkspaceLeaf, LeafView>();
        private WorkspaceLayout m_Layout;
        private bool m_Syncing;
        private bool m_ChromeHidden;
        private IVisualElementScheduledItem m_PendingSave;

        public WorkspaceLayout Layout => m_Layout;
        public WorkspaceLeaf ActiveLeaf => m_Layout.ActiveLeaf;
        public LeafView ActiveLeafView => m_LeafViews.TryGetValue(m_Layout.ActiveLeaf, out var v) ? v : null;
        /// <summary>The editor of the active pane when it shows a note; null for other views.</summary>
        public NoteEditorView ActiveEditor => ActiveLeafView?.ActiveEditor;
        public NoteItem ActiveTabNote => m_Layout.ActiveLeaf.ActiveTab?.Ref as NoteItem;

        /// <summary>The active pane, or what it shows, changed (right sidebar panes rebind).</summary>
        public event Action ActiveContentChanged;
        public event Action<string> ShowSidebarPaneRequested;
        public event Action<string> ExecuteCommandRequested;
        public event Action QuickSwitcherRequested;
        public event Action<EmptyStateAction> EmptyStateActionRequested;
        /// <summary>A pane asked to show a note in the file explorer.</summary>
        public event Action<NoteItem> RevealInExplorerRequested;
        public event Action InsertTemplateRequested;
        /// <summary>A pane asked to show a search (e.g. a tag node of the graph).</summary>
        public event Action<string> SearchRequested;
        /// <summary>Asks for one line of text (placeholder, callback with the trimmed input); set by the window.</summary>
        public Action<string, Action<string>> Prompt;

        private static string LayoutPath => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", "Library", "FuzzyNotesWorkspace.txt");

        public WorkspaceView()
        {
            style.flexGrow = 1;
            style.minWidth = 0;
            style.backgroundColor = NotesTheme.BgPrimary;

            m_Layout = LoadLayout();
            RecordHistory();
            Rebuild();

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                NoteStore.OnActiveNoteChanged += HandleActiveNoteChanged;
                NoteStore.OnNotesChanged += HandleNotesChanged;
                NoteStore.OnNoteSaved += HandleNoteSaved;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                NoteStore.OnActiveNoteChanged -= HandleActiveNoteChanged;
                NoteStore.OnNotesChanged -= HandleNotesChanged;
                NoteStore.OnNoteSaved -= HandleNoteSaved;
                SaveNow();
            });

            // Show the store's active note if the restored layout does not have it in front.
            var active = NoteStore.Instance.ActiveNote;
            if (active != null && ActiveTabNote != active) Open(active);
            else SyncStoreToActive();
        }

        // =====================================================================
        // Opening and closing
        // =====================================================================

        public static string NoteKey(NoteItem note) => NotePrefix + (note.FilePath ?? note.Title);

        public void Open(NoteItem note, NoteOpenMode mode = NoteOpenMode.Default)
        {
            if (note == null) return;
            var leaf = m_Layout.ActiveLeaf;
            bool structural = false;
            if (mode != NoteOpenMode.Default)
            {
                leaf = m_Layout.Split(leaf, mode == NoteOpenMode.SplitDown ? DropZone.Bottom : DropZone.Right);
                structural = true;
            }
            m_Layout.Open(leaf, NoteKey(note), note);
            m_Layout.ActiveLeaf = leaf;
            AfterChange(structural);
        }

        /// <summary>Opens (or focuses) a non-note view such as the tasks board in the active pane.</summary>
        public void OpenView(string key)
        {
            m_Layout.Open(m_Layout.ActiveLeaf, key);
            AfterChange(false);
        }

        public void SelectTab(WorkspaceLeaf leaf, int index)
        {
            if (index < 0 || index >= leaf.Tabs.Count) return;
            leaf.ActiveIndex = index;
            m_Layout.ActiveLeaf = leaf;
            AfterChange(false);
        }

        public void CloseTab(WorkspaceLeaf leaf, int index)
        {
            if (index < 0 || index >= leaf.Tabs.Count) return;
            if (leaf.Tabs[index].Ref is NoteItem note)
            {
                if (note.IsDirty) NoteStore.Instance.SaveNote(note, immediate: true);
                if (m_LeafViews.TryGetValue(leaf, out var view)) view.ActiveEditor?.ForgetViewState(note);
            }
            bool structural = leaf.Tabs.Count == 1 && !m_Layout.IsOnlyLeaf(leaf);
            m_Layout.CloseTab(leaf, index);
            AfterChange(structural);
        }

        public void CloseActiveTab()
        {
            var leaf = m_Layout.ActiveLeaf;
            if (leaf.ActiveIndex >= 0) CloseTab(leaf, leaf.ActiveIndex);
            else if (!m_Layout.IsOnlyLeaf(leaf)) ClosePane(leaf);
        }

        public void ClosePane(WorkspaceLeaf leaf)
        {
            if (m_Layout.IsOnlyLeaf(leaf)) return;
            foreach (var tab in leaf.Tabs)
                if (tab.Ref is NoteItem note && note.IsDirty) NoteStore.Instance.SaveNote(note, immediate: true);
            m_Layout.RemoveLeaf(leaf);
            AfterChange(true);
        }

        private void CloseWhere(WorkspaceLeaf leaf, Func<int, bool> shouldClose)
        {
            for (int i = leaf.Tabs.Count - 1; i >= 0; i--)
            {
                if (!shouldClose(i)) continue;
                if (leaf.Tabs[i].Ref is NoteItem note && note.IsDirty) NoteStore.Instance.SaveNote(note, immediate: true);
                leaf.Tabs.RemoveAt(i);
                if (i < leaf.ActiveIndex) leaf.ActiveIndex--;
            }
            leaf.ActiveIndex = Mathf.Clamp(leaf.ActiveIndex, leaf.Tabs.Count == 0 ? -1 : 0, leaf.Tabs.Count - 1);
            AfterChange(false);
        }

        public void CycleTab(int direction)
        {
            var leaf = m_Layout.ActiveLeaf;
            if (leaf.Tabs.Count < 2) return;
            SelectTab(leaf, ((leaf.ActiveIndex + direction) % leaf.Tabs.Count + leaf.Tabs.Count) % leaf.Tabs.Count);
        }

        public void FocusNextPane()
        {
            var next = m_Layout.Neighbor(m_Layout.ActiveLeaf, 1);
            Activate(next);
            if (m_LeafViews.TryGetValue(next, out var view)) view.ActiveEditor?.FocusEditor();
        }

        /// <summary>Back (-1) or forward (+1) in a pane's history; the item reopens in that pane.</summary>
        public void Navigate(WorkspaceLeaf leaf, int direction)
        {
            var entry = direction < 0 ? leaf.History.GoBack() : leaf.History.GoForward();
            if (entry == null) return;
            m_Layout.Open(leaf, entry.Key, entry.Ref);
            m_Layout.ActiveLeaf = leaf;
            AfterChange(false);
        }

        public void NavigateActive(int direction) => Navigate(m_Layout.ActiveLeaf, direction);

        /// <summary>Focus mode: hides the tab strips and pane headers.</summary>
        public void SetChromeHidden(bool hidden)
        {
            m_ChromeHidden = hidden;
            foreach (var view in m_LeafViews.Values) view.SetChromeHidden(hidden);
        }

        /// <summary>Like Obsidian's "Split right/down": the active tab is shown in a new pane next to this one.</summary>
        public void SplitActive(DropZone side)
        {
            var leaf = m_Layout.ActiveLeaf;
            var tab = leaf.ActiveTab;
            var created = m_Layout.Split(leaf, side);
            if (tab != null) m_Layout.Open(created, tab.Key, tab.Ref);
            m_Layout.ActiveLeaf = created;
            AfterChange(true);
        }

        /// <summary>Makes a pane the active one (pointer or focus inside it).</summary>
        public void Activate(WorkspaceLeaf leaf)
        {
            if (m_Layout.ActiveLeaf == leaf || !m_LeafViews.ContainsKey(leaf)) return;
            m_Layout.ActiveLeaf = leaf;
            // restyle only: rebuilding the tab strips here would destroy the tab under the pointer
            foreach (var kv in m_LeafViews) kv.Value.SetFocused(kv.Key == leaf);
            SyncStoreToActive();
            ActiveContentChanged?.Invoke();
            ScheduleSave();
        }

        // =====================================================================
        // Drag & drop
        // =====================================================================

        public void DropOnPane(WorkspaceLeaf target, object payload, DropZone zone)
        {
            if (payload is TabDragData drag)
            {
                if (zone == DropZone.Center) m_Layout.MoveTab(drag.Leaf, drag.Index, target, target.Tabs.Count);
                else m_Layout.MoveTabToSplit(drag.Leaf, drag.Index, target, zone);
            }
            else if (payload is NoteItem note)
            {
                var leaf = zone == DropZone.Center ? target : m_Layout.Split(target, zone);
                m_Layout.Open(leaf, NoteKey(note), note);
                m_Layout.ActiveLeaf = leaf;
            }
            AfterChange(true);
        }

        public void DropOnTabStrip(WorkspaceLeaf target, object payload, int insertIndex)
        {
            if (payload is TabDragData drag) m_Layout.MoveTab(drag.Leaf, drag.Index, target, insertIndex);
            else if (payload is NoteItem note)
            {
                m_Layout.Open(target, NoteKey(note), note);
                int at = target.IndexOf(t => ReferenceEquals(t.Ref, note));
                if (at >= 0) m_Layout.MoveTab(target, at, target, insertIndex);
                m_Layout.ActiveLeaf = target;
            }
            AfterChange(true);
        }

        // =====================================================================
        // Tab menu
        // =====================================================================

        public void ShowTabMenu(WorkspaceLeaf leaf, int index)
        {
            if (index < 0 || index >= leaf.Tabs.Count) return;
            var tab = leaf.Tabs[index];
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent($"Close\t{ShortcutText(IDEAction.NoteCloseTab)}"), false, () => CloseTab(leaf, leaf.Tabs.IndexOf(tab)));
            menu.AddItem(new GUIContent("Close Others"), false, () => CloseWhere(leaf, i => leaf.Tabs[i] != tab));
            menu.AddItem(new GUIContent("Close Tabs to the Right"), false, () =>
            {
                int at = leaf.Tabs.IndexOf(tab);
                CloseWhere(leaf, i => i > at);
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent($"Split Right\t{ShortcutText(IDEAction.NoteSplitRight)}"), false, () => { SelectTab(leaf, leaf.Tabs.IndexOf(tab)); SplitActive(DropZone.Right); });
            menu.AddItem(new GUIContent($"Split Down\t{ShortcutText(IDEAction.NoteSplitDown)}"), false, () => { SelectTab(leaf, leaf.Tabs.IndexOf(tab)); SplitActive(DropZone.Bottom); });
            if (!m_Layout.IsOnlyLeaf(leaf)) menu.AddItem(new GUIContent("Close Pane"), false, () => ClosePane(leaf));

            if (tab.Ref is NoteItem note)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent(note.IsPinned ? "Remove Bookmark" : "Bookmark"), false, () => NoteStore.Instance.TogglePin(note));
                menu.AddItem(new GUIContent("Copy Link"), false, () => EditorGUIUtility.systemCopyBuffer = $"[[{note.LinkName}]]");
                menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () => { if (File.Exists(note.FilePath)) EditorUtility.RevealInFinder(note.FilePath); });
            }
            menu.ShowAsContext();
        }

        public static NoteTabsBar.TabInfo DescribeTab(WorkspaceTab tab)
        {
            if (tab.Ref is NoteItem note)
                return new NoteTabsBar.TabInfo { Title = note.Title, Icon = "file-text", Tooltip = note.RelativePath ?? note.FilePath, Dirty = note.IsDirty };
            if (tab.Key == GraphKey)
                return new NoteTabsBar.TabInfo { Title = "Graph view", Icon = "waypoints", Tooltip = "Graph of all notes" };
            if (tab.Key == TasksKey)
                return new NoteTabsBar.TabInfo { Title = "Tasks", Icon = "square-kanban", Tooltip = "Tasks board" };
            return new NoteTabsBar.TabInfo { Title = tab.Key, Icon = "file-text", Tooltip = "Not found: " + tab.Key };
        }

        public static string ShortcutText(IDEAction action)
        {
            var binding = CodeIDESettings.Current.Keybindings.Bindings.Find(b => b.Action == action)?.Binding;
            return binding != null ? binding.ToString().Replace("Control", "Ctrl").Replace(", ", "+") : "";
        }

        // Called by panes
        internal void RequestQuickSwitcher(WorkspaceLeaf leaf)
        {
            Activate(leaf);
            QuickSwitcherRequested?.Invoke();
        }

        internal void RaiseExecuteCommand(string command) => ExecuteCommandRequested?.Invoke(command);
        internal void RaiseShowSidebarPane(string pane) => ShowSidebarPaneRequested?.Invoke(pane);
        internal void RaiseEmptyStateAction(EmptyStateAction action) => EmptyStateActionRequested?.Invoke(action);
        internal void RaiseRevealInExplorer(NoteItem note) => RevealInExplorerRequested?.Invoke(note);
        internal void RaiseInsertTemplate() => InsertTemplateRequested?.Invoke();
        internal void RaiseSearch(string query) => SearchRequested?.Invoke(query);

        internal void OnLeafContentChanged(LeafView view)
        {
            if (view.Leaf == m_Layout.ActiveLeaf) ActiveContentChanged?.Invoke();
        }

        // =====================================================================
        // Store sync
        // =====================================================================

        private void HandleActiveNoteChanged(NoteItem note)
        {
            if (m_Syncing || note == null || ActiveTabNote == note) return;
            Open(note);
        }

        private void HandleNotesChanged()
        {
            var store = NoteStore.Instance;
            var live = new HashSet<NoteItem>(store.Notes);

            // tabs restored before their note was loaded, or whose note reappeared
            foreach (var tab in m_Layout.AllTabs())
                if (tab.Ref == null && tab.Key.StartsWith(NotePrefix, StringComparison.Ordinal))
                    tab.Ref = store.FindByPath(tab.Key.Substring(NotePrefix.Length));

            int leavesBefore = m_Layout.Leaves().Count;
            int removed = m_Layout.RemoveTabs(t => t.Key.StartsWith(NotePrefix, StringComparison.Ordinal) && !(t.Ref is NoteItem n && live.Contains(n)));
            foreach (var leaf in m_Layout.Leaves())
                leaf.History.Remove(e => e.Key.StartsWith(NotePrefix, StringComparison.Ordinal) && !(e.Ref is NoteItem n && live.Contains(n)));
            AfterChange(removed > 0 && m_Layout.Leaves().Count != leavesBefore, syncStore: removed > 0);
        }

        private void HandleNoteSaved(NoteItem note) => RefreshPanes();

        private void SyncStoreToActive()
        {
            m_Syncing = true;
            try { NoteStore.Instance.SetActiveNote(ActiveTabNote); }
            finally { m_Syncing = false; }
        }

        private void AfterChange(bool structural, bool syncStore = true)
        {
            RecordHistory();
            if (structural) Rebuild();
            else RefreshPanes();
            if (syncStore) SyncStoreToActive();
            ActiveContentChanged?.Invoke();
            ScheduleSave();
        }

        /// <summary>Every pane remembers what it shows now (no-op when it did not change).</summary>
        private void RecordHistory()
        {
            foreach (var leaf in m_Layout.Leaves())
            {
                var tab = leaf.ActiveTab;
                if (tab != null && (tab.Ref != null || !tab.Key.StartsWith(NotePrefix, StringComparison.Ordinal)))
                    leaf.History.Visit(tab.Key, tab.Ref);
            }
        }

        // =====================================================================
        // Rendering
        // =====================================================================

        /// <summary>Rebuilds the split tree. Pane views (and their editors) are reused.</summary>
        public void Rebuild()
        {
            var leaves = new HashSet<WorkspaceLeaf>(m_Layout.Leaves());
            foreach (var gone in m_LeafViews.Keys.Where(l => !leaves.Contains(l)).ToList())
            {
                m_LeafViews[gone].RemoveFromHierarchy();
                m_LeafViews.Remove(gone);
            }

            Clear();
            Add(Build(m_Layout.Root));
            RefreshPanes();
        }

        private VisualElement Build(WorkspaceNode node)
        {
            if (node is WorkspaceLeaf leaf)
            {
                if (!m_LeafViews.TryGetValue(leaf, out var view))
                {
                    view = new LeafView(this, leaf);
                    view.SetChromeHidden(m_ChromeHidden);
                    m_LeafViews[leaf] = view;
                }
                return view;
            }

            var split = (WorkspaceSplit)node;
            var orientation = split.Vertical ? TwoPaneSplitViewOrientation.Vertical : TwoPaneSplitViewOrientation.Horizontal;
            var splitView = new TwoPaneSplitView(0, split.FirstSize > 0 ? split.FirstSize : 300f, orientation) { style = { flexGrow = 1 } };
            var first = Build(split.First);
            var second = Build(split.Second);
            splitView.Add(first);
            splitView.Add(second);

            // First layout without a stored size: split the space evenly. Afterwards remember what the user drags.
            first.RegisterCallback<GeometryChangedEvent>(e =>
            {
                float total = split.Vertical ? splitView.layout.height : splitView.layout.width;
                if (float.IsNaN(total) || total <= 0) return;
                if (split.FirstSize <= 0)
                {
                    split.FirstSize = Mathf.Round(total / 2f);
                    if (split.Vertical) first.style.height = split.FirstSize; else first.style.width = split.FirstSize;
                    return;
                }
                float size = split.Vertical ? e.newRect.height : e.newRect.width;
                if (size > 0 && Mathf.Abs(size - split.FirstSize) > 0.5f)
                {
                    split.FirstSize = size;
                    ScheduleSave();
                }
            });
            return splitView;
        }

        /// <summary>Re-renders every pane's tabs and content (no structural change).</summary>
        public void RefreshPanes()
        {
            foreach (var kv in m_LeafViews)
                kv.Value.Refresh(kv.Key == m_Layout.ActiveLeaf);
        }

        // =====================================================================
        // Persistence
        // =====================================================================

        private void ScheduleSave()
        {
            m_PendingSave?.Pause();
            m_PendingSave = schedule.Execute(SaveNow).StartingIn(500);
        }

        public void SaveNow()
        {
            m_PendingSave?.Pause();
            foreach (var tab in m_Layout.AllTabs())
                if (tab.Ref is NoteItem note) tab.Key = NoteKey(note);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath));
                File.WriteAllText(LayoutPath, m_Layout.Serialize());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FuzzyNotes] Could not save the workspace layout: {e.Message}");
            }
        }

        private static WorkspaceLayout LoadLayout()
        {
            WorkspaceLayout layout;
            if (File.Exists(LayoutPath))
            {
                string text = "";
                try { text = File.ReadAllText(LayoutPath); }
                catch (Exception e) { Debug.LogWarning($"[FuzzyNotes] Could not read the workspace layout: {e.Message}"); }
                layout = WorkspaceLayout.Deserialize(text);
            }
            else
            {
                // first run after the update: bring over the tabs of the single-pane editor
                layout = new WorkspaceLayout();
                foreach (var path in SessionState.GetString(LegacyTabsSessionKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    layout.Open(layout.ActiveLeaf, NotePrefix + path);
            }

            var store = NoteStore.Instance;
            foreach (var tab in layout.AllTabs())
                if (tab.Key.StartsWith(NotePrefix, StringComparison.Ordinal))
                    tab.Ref = store.FindByPath(tab.Key.Substring(NotePrefix.Length));
            layout.RemoveTabs(t => t.Key.StartsWith(NotePrefix, StringComparison.Ordinal) && t.Ref == null);
            return layout;
        }
    }
}
