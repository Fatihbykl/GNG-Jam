using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Files pane of the left sidebar, like Obsidian's file explorer: a virtualized tree of vaults, folders and
    /// notes with keyboard navigation (arrows, Enter, F2, Delete), inline rename, multi-select, indent guides and
    /// drag &amp; drop. Searching lives in its own pane (<see cref="SearchView"/>).
    /// Click opens a note; Alt+click opens it to the right; Ctrl/Shift+click selects.
    /// </summary>
    public class FileExplorerView : VisualElement
    {
        private enum SortMode { Name, Modified, Created }

        public const string DragNoteKey = "FuzzyNotes.DragNote";
        public const string DragNotesKey = "FuzzyNotes.DragNotes";
        public const string DragFolderKey = "FuzzyNotes.DragFolder";

        private const string ExpandedPrefKey = "FuzzyNotes.ExpandedFolders";
        private const string CollapsedVaultsPrefKey = "FuzzyNotes.CollapsedVaults";
        private const string SortPrefKey = "FuzzyNotes.SortMode";
        private const float RowHeight = 26f;
        private const float IndentWidth = 16f;
        private const float ArrowWidth = 18f;

        private sealed class FolderRef
        {
            public NoteVault Vault;
            public string Folder;
        }

        /// <summary>Elements of one recycled list row.</summary>
        private sealed class RowElements
        {
            public VisualElement Indent;
            public VisualElement Arrow;
            public NotesIcon ArrowIcon;
            public NotesIcon Icon;
            public Label Label;
            public TextField Rename;
            public VisualElement Dirty;
            public Label Meta;
            public ExplorerRow Data;
        }

        private ListView m_List;
        private List<ExplorerRow> m_Rows = new List<ExplorerRow>();
        private readonly HashSet<string> m_Expanded;
        private readonly HashSet<string> m_CollapsedVaults;
        private SortMode m_Sort;
        private IVisualElementScheduledItem m_PendingRefresh;
        private string m_RenamingKey;

        public event Action OnNewNoteClicked;
        public event Action OnOpenExternalClicked;
        public event Action<IReadOnlyList<NoteItem>> OnMoveNotesRequested;

        /// <summary>Where "New note" creates notes: the selected folder, or the active note's folder.</summary>
        public NoteVault TargetVault { get; private set; }
        public string TargetFolder { get; private set; } = "";

        public FileExplorerView()
        {
            style.flexGrow = 1;
            style.flexDirection = FlexDirection.Column;
            style.backgroundColor = NotesTheme.BgSecondary;

            m_Expanded = new HashSet<string>(EditorPrefs.GetString(ExpandedPrefKey, "").Split('\n').Where(s => s.Length > 0));
            m_CollapsedVaults = new HashSet<string>(EditorPrefs.GetString(CollapsedVaultsPrefKey, "").Split('\n').Where(s => s.Length > 0));
            m_Sort = (SortMode)EditorPrefs.GetInt(SortPrefKey, (int)SortMode.Modified);

            BuildUI();

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged += RequestRefresh;
                NoteStore.OnActiveNoteChanged += OnActiveNoteChanged;
                NoteStore.OnNoteSaved += OnNoteSaved;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged -= RequestRefresh;
                NoteStore.OnActiveNoteChanged -= OnActiveNoteChanged;
                NoteStore.OnNoteSaved -= OnNoteSaved;
            });
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private void BuildUI()
        {
            var nav = new VisualElement { style = { borderBottomColor = NotesTheme.BorderSubtle, justifyContent = Justify.Center } }.WithClass("fn-toolbar");
            nav.Add(NotesIcons.IconButton("file-plus", "New note", () => OnNewNoteClicked?.Invoke()));
            nav.Add(NotesIcons.IconButton("folder-plus", "New folder", () =>
            {
                // Folders are never created in the Assets vault; fall back to the project vault's root.
                bool assets = TargetVault == null || TargetVault.IsProjectFiles;
                CreateFolderInline(assets ? NoteStore.Instance.PrimaryVault : TargetVault, assets ? "" : TargetFolder);
            }));
            nav.Add(NotesIcons.IconButton("arrow-up-down", "Change sort order", ShowSortMenu));
            nav.Add(NotesIcons.IconButton("chevrons-down-up", "Collapse all", CollapseAll));
            nav.Add(NotesIcons.IconButton("locate-fixed", "Reveal the active note", () => RevealNote(NoteStore.Instance.ActiveNote, select: true)));
            nav.Add(NotesIcons.IconButton("ellipsis", "More", ShowMoreMenu));
            Add(nav);

            m_List = new ListView
            {
                fixedItemHeight = RowHeight,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.Multiple,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = m_Rows,
                style = { flexGrow = 1 }
            }.WithClass("fn-explorer");
            m_List.itemsChosen += OnItemsChosen;
            m_List.selectionChanged += _ => UpdateTargetFromSelection();
            m_List.RegisterCallback<KeyDownEvent>(OnListKeyDown, TrickleDown.TrickleDown);
            Add(m_List);

            RefreshNow();
        }

        // =====================================================================
        // Tree rows
        // =====================================================================

        private void RequestRefresh()
        {
            m_PendingRefresh?.Pause();
            m_PendingRefresh = schedule.Execute(RefreshNow).StartingIn(30);
        }

        private void RefreshNow()
        {
            if (m_RenamingKey != null) return; // the rename commit refreshes

            var store = NoteStore.Instance;
            var selectedKeys = new HashSet<string>(m_List.selectedIndices.Where(i => i >= 0 && i < m_Rows.Count).Select(i => m_Rows[i].Key));

            m_Rows = ExplorerTree.Build(store.Vaults, store.Notes, store.GetFolders,
                (vault, folder) => m_Expanded.Contains(Key(vault, folder)),
                IsVaultCollapsed,
                Sort);
            m_List.itemsSource = m_Rows;

            var indices = new List<int>();
            for (int i = 0; i < m_Rows.Count; i++)
                if (selectedKeys.Contains(m_Rows[i].Key)) indices.Add(i);
            if (indices.Count == 0)
            {
                int active = IndexOfNote(store.ActiveNote);
                if (active >= 0) indices.Add(active);
            }
            m_List.SetSelectionWithoutNotify(indices);
            m_List.RefreshItems();
        }

        private IEnumerable<NoteItem> Sort(IEnumerable<NoteItem> notes)
        {
            switch (m_Sort)
            {
                case SortMode.Name: return notes.OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase);
                case SortMode.Created: return notes.OrderByDescending(n => n.CreatedAt);
                default: return notes.OrderByDescending(n => n.UpdatedAt);
            }
        }

        private VisualElement MakeRow()
        {
            var e = new RowElements();
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, height = RowHeight, paddingRight = 8 } };
            row.userData = e;

            e.Indent = new VisualElement { pickingMode = PickingMode.Ignore, style = { flexDirection = FlexDirection.Row, height = Length.Percent(100), flexShrink = 0 } };
            row.Add(e.Indent);

            e.Arrow = new VisualElement { style = { width = ArrowWidth, height = Length.Percent(100), alignItems = Align.Center, justifyContent = Justify.Center, flexShrink = 0 } };
            e.ArrowIcon = NotesIcons.Create("chevron-right", 14f, NotesTheme.TextFaint);
            e.Arrow.Add(e.ArrowIcon);
            row.Add(e.Arrow);

            e.Icon = NotesIcons.Create("file-text", 15f, NotesTheme.TextFaint);
            e.Icon.style.marginRight = 6;
            row.Add(e.Icon);

            e.Label = new Label().WithClass("fn-row-label");
            row.Add(e.Label);

            e.Rename = new TextField { style = { flexGrow = 1, display = DisplayStyle.None } }.WithClass("fn-input");
            e.Rename.RegisterCallback<KeyDownEvent>(ev =>
            {
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) { CommitRename(e, e.Rename.value); ev.StopPropagation(); }
                else if (ev.keyCode == KeyCode.Escape) { CommitRename(e, null); ev.StopPropagation(); }
                else ev.StopPropagation(); // keep typing away from list navigation
            }, TrickleDown.TrickleDown);
            e.Rename.RegisterCallback<FocusOutEvent>(_ => { if (m_RenamingKey != null && e.Data?.Key == m_RenamingKey) CommitRename(e, e.Rename.value); });
            row.Add(e.Rename);

            e.Dirty = new VisualElement
            {
                tooltip = "Unsaved changes",
                style =
                {
                    width = 6, height = 6, marginLeft = 6, flexShrink = 0,
                    borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                    backgroundColor = NotesTheme.TextMuted
                }
            };
            row.Add(e.Dirty);

            e.Meta = new Label { style = { color = NotesTheme.TextFaint } }.WithClass("fn-row-meta");
            row.Add(e.Meta);

            row.RegisterCallback<ClickEvent>(ev => OnRowClicked(e.Data, ev));
            row.RegisterCallback<ContextClickEvent>(ev => { ShowRowMenu(e.Data); ev.StopPropagation(); });
            MakeDragSource(row, () => e.Data);
            MakeDropTarget(row, () => e.Data);
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var e = (RowElements)element.userData;
            var r = m_Rows[index];
            e.Data = r;
            var store = NoteStore.Instance;

            // indent guides: one 16px column per level with a faint vertical line
            int guides = r.Depth;
            while (e.Indent.childCount < guides)
                e.Indent.Add(new VisualElement { pickingMode = PickingMode.Ignore, style = { width = IndentWidth, marginLeft = 0, borderLeftWidth = 1, borderLeftColor = NotesTheme.BorderSubtle, flexShrink = 0 } });
            for (int i = 0; i < e.Indent.childCount; i++)
            {
                var g = e.Indent[i];
                g.style.display = i < guides ? DisplayStyle.Flex : DisplayStyle.None;
                g.style.marginLeft = i == 0 ? 12f : 0f;
                g.style.width = i == 0 ? IndentWidth - 12f + 8f : IndentWidth;
            }
            e.Indent.style.marginLeft = guides == 0 ? 4f : 0f;

            bool container = r.Kind == ExplorerRowKind.Folder || r.Kind == ExplorerRowKind.Vault;
            e.Arrow.style.visibility = container ? Visibility.Visible : Visibility.Hidden;
            e.Arrow.style.display = r.Kind == ExplorerRowKind.Section ? DisplayStyle.None : DisplayStyle.Flex;
            e.ArrowIcon.SetIcon(r.Expanded ? "chevron-down" : "chevron-right");

            bool active = r.Kind == ExplorerRowKind.Note && r.Note == store.ActiveNote;
            string icon;
            Color iconColor = NotesTheme.TextFaint;
            switch (r.Kind)
            {
                case ExplorerRowKind.Vault: icon = r.Vault.IsPrimary ? "box" : r.Vault.IsProjectFiles ? "square-code" : r.Vault.Id == NoteVault.PersonalId ? "pin" : "link"; iconColor = NotesTheme.TextMuted; break;
                case ExplorerRowKind.Folder: icon = r.Expanded ? "folder-open" : "folder"; iconColor = NotesTheme.TextMuted; break;
                case ExplorerRowKind.Section: icon = "file-symlink"; break;
                default: icon = r.Note.IsExternal ? "file-symlink" : r.Note.IsPinned ? "star" : "file-text"; if (r.Note.IsPinned) iconColor = NotesTheme.Warning; break;
            }
            e.Icon.SetIcon(icon);
            e.Icon.SetColor(iconColor);

            e.Label.text = string.IsNullOrWhiteSpace(r.Label) ? "Untitled" : r.Label;
            e.Label.tooltip = r.Kind == ExplorerRowKind.Note ? r.Note.RelativePath ?? r.Note.FilePath : r.Kind == ExplorerRowKind.Vault ? r.Vault.Root : r.Folder;
            e.Label.style.color = r.Kind == ExplorerRowKind.Note && !active ? NotesTheme.TextMuted : r.Kind == ExplorerRowKind.Section ? NotesTheme.TextFaint : NotesTheme.TextNormal;
            e.Label.style.unityFontStyleAndWeight = r.Kind == ExplorerRowKind.Vault || r.Kind == ExplorerRowKind.Section ? FontStyle.Bold : FontStyle.Normal;

            bool renaming = m_RenamingKey != null && r.Key == m_RenamingKey;
            e.Label.style.display = renaming ? DisplayStyle.None : DisplayStyle.Flex;
            e.Rename.style.display = renaming ? DisplayStyle.Flex : DisplayStyle.None;

            e.Dirty.style.display = r.Kind == ExplorerRowKind.Note && r.Note.IsDirty ? DisplayStyle.Flex : DisplayStyle.None;
            e.Meta.text = r.Kind == ExplorerRowKind.Note ? FormatRelativeTime(r.Note.UpdatedAt) : r.Count > 0 ? r.Count.ToString() : "";
            e.Meta.style.display = renaming ? DisplayStyle.None : DisplayStyle.Flex;

            element.style.backgroundColor = active ? new StyleColor(NotesTheme.BgActive) : new StyleColor(StyleKeyword.Null);
        }

        // =====================================================================
        // Interaction
        // =====================================================================

        private void OnRowClicked(ExplorerRow r, ClickEvent e)
        {
            if (r == null || e.button != 0 || e.clickCount > 1) return;
            if (e.shiftKey || e.ctrlKey || e.commandKey) return; // selection gestures

            switch (r.Kind)
            {
                case ExplorerRowKind.Vault:
                    ToggleVault(r.Vault);
                    break;
                case ExplorerRowKind.Folder:
                    SetTarget(r.Vault, r.Folder);
                    SetExpanded(r.Vault, r.Folder, !r.Expanded);
                    RefreshNow();
                    break;
                case ExplorerRowKind.Note:
                    FuzzyNotesWindow.OpenNote(r.Note, -1, e.altKey ? NoteOpenMode.SplitRight : NoteOpenMode.Default);
                    break;
            }
        }

        /// <summary>Enter or double-click: open the chosen note.</summary>
        private void OnItemsChosen(IEnumerable<object> items)
        {
            var note = items.OfType<ExplorerRow>().FirstOrDefault(r => r.Kind == ExplorerRowKind.Note)?.Note;
            if (note != null) FuzzyNotesWindow.OpenNote(note);
        }

        private void OnListKeyDown(KeyDownEvent e)
        {
            int index = m_List.selectedIndex;
            if (index < 0 || index >= m_Rows.Count) return;
            var r = m_Rows[index];

            switch (e.keyCode)
            {
                case KeyCode.RightArrow:
                    if ((r.Kind == ExplorerRowKind.Folder || r.Kind == ExplorerRowKind.Vault) && !r.Expanded) Expand(r, true);
                    else if (index + 1 < m_Rows.Count && m_Rows[index + 1].Depth > r.Depth) Select(index + 1);
                    break;
                case KeyCode.LeftArrow:
                    if ((r.Kind == ExplorerRowKind.Folder || r.Kind == ExplorerRowKind.Vault) && r.Expanded) Expand(r, false);
                    else
                    {
                        int parent = ExplorerTree.ParentRowIndex(m_Rows, index);
                        if (parent >= 0) Select(parent);
                    }
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (r.Kind == ExplorerRowKind.Note && (e.altKey || e.actionKey))
                        FuzzyNotesWindow.OpenNote(r.Note, -1, NoteOpenMode.SplitRight);
                    else if (r.Kind != ExplorerRowKind.Note) Expand(r, !r.Expanded);
                    else return; // plain Enter on a note: itemsChosen opens it
                    break;
                case KeyCode.F2:
                    StartRename(r);
                    break;
                case KeyCode.Delete:
                    var notes = SelectedNotes();
                    if (notes.Count > 0) NoteActions.DeleteMany(notes);
                    else if (r.Kind == ExplorerRowKind.Folder && !r.Vault.IsProjectFiles) DeleteFolder(r.Vault, r.Folder);
                    break;
                default:
                    return;
            }
            e.StopPropagation();
        }

        private void Select(int index)
        {
            m_List.SetSelection(index);
            m_List.ScrollToItem(index);
        }

        private void Expand(ExplorerRow r, bool expanded)
        {
            if (r.Kind == ExplorerRowKind.Vault) { if (expanded == IsVaultCollapsed(r.Vault)) ToggleVault(r.Vault); return; }
            SetExpanded(r.Vault, r.Folder, expanded);
            RefreshNow();
        }

        private void ToggleVault(NoteVault vault)
        {
            SetVaultCollapsed(vault, !IsVaultCollapsed(vault));
            SetTarget(vault, "");
            RefreshNow();
        }

        // The Assets vault starts collapsed (it can hold many READMEs), so for it the stored entry means "expanded".
        private static string CollapseKey(NoteVault vault) => vault.IsProjectFiles ? "+" + vault.Id : vault.Id;

        private bool IsVaultCollapsed(NoteVault vault) => m_CollapsedVaults.Contains(CollapseKey(vault)) != vault.IsProjectFiles;

        private void SetVaultCollapsed(NoteVault vault, bool collapsed)
        {
            if (vault == null || IsVaultCollapsed(vault) == collapsed) return;
            if (!m_CollapsedVaults.Remove(CollapseKey(vault))) m_CollapsedVaults.Add(CollapseKey(vault));
            EditorPrefs.SetString(CollapsedVaultsPrefKey, string.Join("\n", m_CollapsedVaults));
        }

        private void UpdateTargetFromSelection()
        {
            int index = m_List.selectedIndex;
            if (index < 0 || index >= m_Rows.Count) return;
            var r = m_Rows[index];
            if (r.Kind == ExplorerRowKind.Folder || r.Kind == ExplorerRowKind.Vault) SetTarget(r.Vault, r.Folder);
            else if (r.Kind == ExplorerRowKind.Note && !r.Note.IsExternal) SetTarget(r.Vault, r.Folder);
        }

        private void SetTarget(NoteVault vault, string folder)
        {
            TargetVault = vault;
            TargetFolder = folder ?? "";
        }

        private List<NoteItem> SelectedNotes() =>
            m_List.selectedIndices.Where(i => i >= 0 && i < m_Rows.Count && m_Rows[i].Kind == ExplorerRowKind.Note).Select(i => m_Rows[i].Note).ToList();

        private int IndexOfNote(NoteItem note)
        {
            if (note == null) return -1;
            for (int i = 0; i < m_Rows.Count; i++)
                if (m_Rows[i].Kind == ExplorerRowKind.Note && m_Rows[i].Note == note) return i;
            return -1;
        }

        private void OnActiveNoteChanged(NoteItem note)
        {
            if (note != null && !note.IsExternal) SetTarget(NoteStore.Instance.VaultOf(note), note.Folder);
            RevealNote(note, select: true);
        }

        private void OnNoteSaved(NoteItem note)
        {
            int index = IndexOfNote(note);
            if (index >= 0) m_List.RefreshItem(index);
        }

        /// <summary>Expands the folders above a note, selects it and scrolls it into view.</summary>
        public void Reveal(NoteItem note) => RevealNote(note, select: true);

        /// <summary>Expands the folders above a note and scrolls it into view.</summary>
        private void RevealNote(NoteItem note, bool select)
        {
            if (note != null && !note.IsExternal)
            {
                var vault = NoteStore.Instance.VaultOf(note);
                if (vault != null)
                {
                    for (string p = note.Folder; !string.IsNullOrEmpty(p); p = ExplorerTree.Parent(p)) m_Expanded.Add(Key(vault, p));
                    SetVaultCollapsed(vault, false);
                    SaveExpanded();
                }
            }
            RefreshNow();
            int index = IndexOfNote(note);
            if (index < 0) return;
            if (select) m_List.SetSelectionWithoutNotify(new[] { index });
            m_List.ScrollToItem(index);
        }

        private void CollapseAll()
        {
            m_Expanded.Clear();
            SaveExpanded();
            RefreshNow();
        }

        // =====================================================================
        // Inline rename & new folder
        // =====================================================================

        private void StartRename(ExplorerRow r)
        {
            if (r == null || r.Kind == ExplorerRowKind.Section || r.Kind == ExplorerRowKind.Vault) return;
            if (r.Kind == ExplorerRowKind.Folder && r.Vault.IsProjectFiles) return; // Unity's folders: rename them in the Project window
            m_RenamingKey = r.Key;
            int index = m_Rows.IndexOf(r);
            if (index < 0) { m_RenamingKey = null; return; }
            m_List.ScrollToItem(index);
            m_List.RefreshItem(index);

            // the row element exists after the list has laid out the scrolled position
            schedule.Execute(() =>
            {
                var row = FindRowElements(m_List.GetRootElementForIndex(index));
                if (row == null || row.Data?.Key != m_RenamingKey)
                {
                    m_RenamingKey = null;
                    RefreshNow();
                    return;
                }
                row.Rename.SetValueWithoutNotify(r.Kind == ExplorerRowKind.Note ? r.Note.Title : ExplorerTree.Leaf(r.Folder));
                row.Rename.Focus();
                row.Rename.SelectAll();
            });
        }

        private static RowElements FindRowElements(VisualElement root)
        {
            if (root == null) return null;
            if (root.userData is RowElements e) return e;
            foreach (var child in root.Children())
                if (child.userData is RowElements c) return c;
            return null;
        }

        /// <summary>Applies (or cancels, with null) an inline rename.</summary>
        private void CommitRename(RowElements e, string value)
        {
            var r = e.Data;
            if (m_RenamingKey == null || r == null || r.Key != m_RenamingKey) return;
            m_RenamingKey = null; // first: dialogs below steal focus and would re-enter

            value = value?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                if (r.Kind == ExplorerRowKind.Note && value != r.Note.Title) NoteActions.Rename(r.Note, value);
                else if (r.Kind == ExplorerRowKind.Folder && value != ExplorerTree.Leaf(r.Folder) && !NoteStore.Instance.RenameFolder(r.Vault, r.Folder, value))
                    NoteToast.Show($"Could not rename '{r.Folder}'. A folder with that name may already exist.", NoteToastKind.Error);
            }

            RefreshNow();
            m_List.Focus();
        }

        /// <summary>Creates "New folder" (numbered if taken) under <paramref name="parent"/> and starts renaming it.</summary>
        private void CreateFolderInline(NoteVault vault, string parent)
        {
            if (vault == null) return;
            var existing = new HashSet<string>(NoteStore.Instance.GetFolders(vault), StringComparer.OrdinalIgnoreCase);
            string prefix = parent.Length > 0 ? parent + "/" : "";
            string name = "New folder";
            for (int i = 1; existing.Contains(prefix + name); i++) name = $"New folder {i}";
            if (!NoteStore.Instance.CreateFolder(vault, prefix + name)) return;

            SetExpanded(vault, parent, true);
            SetVaultCollapsed(vault, false);
            SetTarget(vault, prefix + name);
            RefreshNow();
            var row = m_Rows.FirstOrDefault(r => r.Kind == ExplorerRowKind.Folder && r.Vault == vault && string.Equals(r.Folder, prefix + name, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                m_List.SetSelectionWithoutNotify(new[] { m_Rows.IndexOf(row) });
                StartRename(row);
            }
        }

        // =====================================================================
        // Menus
        // =====================================================================

        private void ShowSortMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("File name (A to Z)"), m_Sort == SortMode.Name, () => SetSort(SortMode.Name));
            menu.AddItem(new GUIContent("Modified time (new to old)"), m_Sort == SortMode.Modified, () => SetSort(SortMode.Modified));
            menu.AddItem(new GUIContent("Created time (new to old)"), m_Sort == SortMode.Created, () => SetSort(SortMode.Created));
            menu.ShowAsContext();
        }

        private void SetSort(SortMode mode)
        {
            m_Sort = mode;
            EditorPrefs.SetInt(SortPrefKey, (int)mode);
            RefreshNow();
        }

        private void ShowMoreMenu()
        {
            var store = NoteStore.Instance;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Open Markdown File…"), false, () => OnOpenExternalClicked?.Invoke());
            menu.AddItem(new GUIContent("Vaults, Daily Notes & Templates…"), false, NoteSettingsWindow.Open);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Scan Codebase for TODOs"), false, () => GitNoteBridge.ScanAndCollectProjectTodos());
            menu.AddItem(new GUIContent("Stamp Active Note with Git Branch"), false, () => GitNoteBridge.StampActiveNoteWithGitInfo(store.ActiveNote));
            menu.AddSeparator("");
            if (store.CanRestoreDeleted) menu.AddItem(new GUIContent("Undo Last Delete"), false, () => store.RestoreLastDeleted());
            else menu.AddDisabledItem(new GUIContent("Undo Last Delete"));
            menu.AddItem(new GUIContent("Open Trash Folder"), false, () =>
            {
                Directory.CreateDirectory(store.TrashDirectory);
                EditorUtility.RevealInFinder(store.TrashDirectory + Path.DirectorySeparatorChar);
            });
            menu.AddItem(new GUIContent("Reload Notes from Disk"), false, () => store.CheckExternalChanges());
            menu.AddItem(new GUIContent("Open .fuzzynotes Folder in Explorer"), false, () =>
            {
                if (Directory.Exists(NoteStore.NotesDirectory)) EditorUtility.RevealInFinder(NoteStore.NotesDirectory);
            });
            menu.ShowAsContext();
        }

        private void ShowRowMenu(ExplorerRow r)
        {
            if (r == null) return;
            var store = NoteStore.Instance;
            var menu = new GenericMenu();

            switch (r.Kind)
            {
                case ExplorerRowKind.Vault when r.Vault.IsProjectFiles:
                    menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () => EditorUtility.RevealInFinder(r.Vault.Root + Path.DirectorySeparatorChar));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Hide Assets Markdown Files"), false, () => SetProjectMarkdownShown(false));
                    menu.AddItem(new GUIContent("Excluded Folders & Files…"), false, NoteSettingsWindow.Open);
                    break;

                case ExplorerRowKind.Folder when r.Vault.IsProjectFiles:
                    menu.AddItem(new GUIContent("Show in Project Window"), false, () => PingAsset("Assets/" + r.Folder));
                    menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () => EditorUtility.RevealInFinder(r.Vault.FolderPath(r.Folder) + Path.DirectorySeparatorChar));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Exclude This Folder"), false, () => ExcludeFromProjectMarkdown(r.Folder));
                    break;

                case ExplorerRowKind.Vault:
                    menu.AddItem(new GUIContent("New Note"), false, () => { SetTarget(r.Vault, ""); OnNewNoteClicked?.Invoke(); });
                    menu.AddItem(new GUIContent("New Folder"), false, () => CreateFolderInline(r.Vault, ""));
                    menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () => EditorUtility.RevealInFinder(r.Vault.Root + Path.DirectorySeparatorChar));
                    if (!r.Vault.IsPrimary)
                    {
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Vault Settings…"), false, NoteSettingsWindow.Open);
                    }
                    break;

                case ExplorerRowKind.Folder:
                    menu.AddItem(new GUIContent("New Note Here"), false, () => { SetTarget(r.Vault, r.Folder); OnNewNoteClicked?.Invoke(); });
                    menu.AddItem(new GUIContent("New Folder Here"), false, () => CreateFolderInline(r.Vault, r.Folder));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Rename\tF2"), false, () => StartRename(r));
                    menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () => EditorUtility.RevealInFinder(r.Vault.FolderPath(r.Folder) + Path.DirectorySeparatorChar));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Move Folder to Trash"), false, () => DeleteFolder(r.Vault, r.Folder));
                    break;

                case ExplorerRowKind.Note:
                    var selected = SelectedNotes();
                    if (selected.Count > 1 && selected.Contains(r.Note))
                    {
                        var movable = selected.Where(n => !n.IsExternal && store.VaultOf(n)?.IsProjectFiles != true).ToList();
                        if (movable.Count > 0) menu.AddItem(new GUIContent($"Move {movable.Count} Notes to…"), false, () => OnMoveNotesRequested?.Invoke(movable));
                        menu.AddItem(new GUIContent($"Move {selected.Count} Notes to Trash\tDel"), false, () => NoteActions.DeleteMany(selected));
                        break;
                    }
                    var note = r.Note;
                    bool projectFile = r.Vault != null && r.Vault.IsProjectFiles;
                    menu.AddItem(new GUIContent("Open"), false, () => FuzzyNotesWindow.OpenNote(note));
                    menu.AddItem(new GUIContent("Open to the Right"), false, () => FuzzyNotesWindow.OpenNote(note, -1, NoteOpenMode.SplitRight));
                    menu.AddItem(new GUIContent("Open Below"), false, () => FuzzyNotesWindow.OpenNote(note, -1, NoteOpenMode.SplitDown));
                    menu.AddItem(new GUIContent("Open as Source in Fuzzy IDE"), false, () => MarkdownAssets.OpenSourceInIde(note));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Rename\tF2"), false, () => StartRename(r));
                    if (!note.IsExternal && !projectFile) menu.AddItem(new GUIContent("Move to…"), false, () => OnMoveNotesRequested?.Invoke(new[] { note }));
                    menu.AddItem(new GUIContent(note.IsPinned ? "Remove Bookmark" : "Bookmark"), false, () => store.TogglePin(note));
                    menu.AddItem(new GUIContent("Copy Link"), false, () => EditorGUIUtility.systemCopyBuffer = $"[[{note.LinkName}]]");
                    menu.AddSeparator("");
                    if (projectFile) menu.AddItem(new GUIContent("Show in Project Window"), false, () => PingAsset("Assets/" + note.RelativePath));
                    menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () => { if (File.Exists(note.FilePath)) EditorUtility.RevealInFinder(note.FilePath); });
                    if (projectFile) menu.AddItem(new GUIContent("Exclude This File"), false, () => ExcludeFromProjectMarkdown(note.RelativePath));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent(note.IsExternal ? "Close External Note" : "Move to Trash\tDel"), false, () => NoteActions.Delete(note));
                    break;

                default:
                    return;
            }
            menu.ShowAsContext();
        }

        private static void PingAsset(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null) return;
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>Adds an Assets-relative folder or file to the exclusion list and reloads the vaults.</summary>
        private static void ExcludeFromProjectMarkdown(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return;
            var excludes = NoteSettings.ProjectMarkdownExcludes;
            // A pattern with '/' is matched as a path from the Assets folder, so a top-level name needs the prefix.
            string pattern = relativePath.IndexOf('/') >= 0 ? relativePath : "Assets/" + relativePath;
            if (!excludes.Contains(pattern, StringComparer.OrdinalIgnoreCase)) excludes.Add(pattern);
            NoteSettings.ProjectMarkdownExcludes = excludes;
            NoteStore.Instance.ReloadVaultConfiguration();
            NoteToast.Show($"'{relativePath}' is hidden from Fuzzy Notes. Edit the list in Settings › General.", NoteToastKind.Info);
        }

        private static void SetProjectMarkdownShown(bool shown)
        {
            NoteSettings.ShowProjectMarkdown = shown;
            NoteStore.Instance.ReloadVaultConfiguration();
            if (!shown) NoteToast.Show("Assets markdown files are hidden. Turn them back on in Settings › General.", NoteToastKind.Info);
        }

        private static void DeleteFolder(NoteVault vault, string folder)
        {
            // a folder can hold many notes: still ask, then offer undo like single notes
            if (EditorUtility.DisplayDialog("Delete Folder", $"Move '{folder}' and all notes inside it to the trash?", "Move to Trash", "Cancel") &&
                NoteStore.Instance.DeleteFolder(vault, folder))
                NoteToast.Show($"Moved '{folder}' to the trash", NoteToastKind.Info, "Undo", () => NoteStore.Instance.RestoreLastDeleted(), 8000);
        }

        // =====================================================================
        // Drag & drop (notes and folders onto folders/vaults)
        // =====================================================================

        private void MakeDragSource(VisualElement row, Func<ExplorerRow> data)
        {
            Vector2 start = Vector2.zero;
            bool armed = false;

            row.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                armed = true;
                start = e.position;
            });
            row.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!armed || (e.pressedButtons & 1) == 0) { armed = false; return; }
                if (((Vector2)e.position - start).sqrMagnitude < 36f) return;
                armed = false;

                var r = data();
                if (r == null || r.Kind == ExplorerRowKind.Section || r.Kind == ExplorerRowKind.Vault || m_RenamingKey != null) return;
                if (r.Kind == ExplorerRowKind.Note && r.Note.IsExternal) return;
                if (r.Vault != null && r.Vault.IsProjectFiles) return; // Assets files are moved in the Project window

                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new UnityEngine.Object[0];
                DragAndDrop.paths = new string[0];
                string title;
                if (r.Kind == ExplorerRowKind.Note)
                {
                    var selected = SelectedNotes().Where(n => !n.IsExternal && NoteStore.Instance.VaultOf(n)?.IsProjectFiles != true).ToList();
                    if (selected.Count > 1 && selected.Contains(r.Note))
                    {
                        DragAndDrop.SetGenericData(DragNotesKey, selected);
                        title = $"{selected.Count} notes";
                    }
                    else title = r.Note.Title;
                    DragAndDrop.SetGenericData(DragNoteKey, r.Note);
                }
                else
                {
                    DragAndDrop.SetGenericData(DragFolderKey, new FolderRef { Vault = r.Vault, Folder = r.Folder });
                    title = ExplorerTree.Leaf(r.Folder);
                }
                DragAndDrop.StartDrag(title);
            });
            row.RegisterCallback<PointerUpEvent>(_ => armed = false);
        }

        /// <summary>Folder a row stands for when something is dropped on it (a note's own folder for note rows).</summary>
        private static bool DropTarget(ExplorerRow r, out NoteVault vault, out string folder)
        {
            vault = r?.Vault;
            folder = r?.Folder ?? "";
            if (r == null || r.Kind == ExplorerRowKind.Section || vault == null || vault.IsProjectFiles) return false;
            if (r.Kind == ExplorerRowKind.Note && r.Note.IsExternal) return false;
            return true;
        }

        private void MakeDropTarget(VisualElement row, Func<ExplorerRow> data)
        {
            row.RegisterCallback<DragUpdatedEvent>(e =>
            {
                if (!DropTarget(data(), out var vault, out var folder) || !CanDrop(vault, folder)) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Move;
                row.style.backgroundColor = NotesTheme.AccentSoft;
                e.StopPropagation();
            });
            row.RegisterCallback<DragLeaveEvent>(_ => row.style.backgroundColor = StyleKeyword.Null);
            row.RegisterCallback<DragExitedEvent>(_ => row.style.backgroundColor = StyleKeyword.Null);
            row.RegisterCallback<DragPerformEvent>(e =>
            {
                if (!DropTarget(data(), out var vault, out var folder) || !CanDrop(vault, folder)) return;
                DragAndDrop.AcceptDrag();
                row.style.backgroundColor = StyleKeyword.Null;

                if (DragAndDrop.GetGenericData(DragNotesKey) is List<NoteItem> many)
                {
                    var failed = many.Where(n => !NoteStore.Instance.MoveNote(n, vault, folder)).Select(n => n.Title).ToList();
                    if (failed.Count > 0)
                        NoteToast.Show($"Could not move: {string.Join(", ", failed)}. A note with the same name may already exist there.", NoteToastKind.Error);
                }
                else if (DragAndDrop.GetGenericData(DragNoteKey) is NoteItem note)
                {
                    if (!NoteStore.Instance.MoveNote(note, vault, folder))
                        NoteToast.Show($"Could not move '{note.Title}'. A note with the same name may already exist there.", NoteToastKind.Error);
                }
                else if (DragAndDrop.GetGenericData(DragFolderKey) is FolderRef source)
                {
                    string target = (folder.Length > 0 ? folder + "/" : "") + ExplorerTree.Leaf(source.Folder);
                    if (source.Vault != vault || !NoteStore.Instance.MoveFolder(vault, source.Folder, target))
                        NoteToast.Show("Folders can only be moved within the same vault, and not into themselves or onto an existing folder.", NoteToastKind.Error);
                    else SetExpanded(vault, folder, true);
                }
                ClearDrag();
                e.StopPropagation();
            });
        }

        private static void ClearDrag()
        {
            DragAndDrop.SetGenericData(DragNoteKey, null);
            DragAndDrop.SetGenericData(DragNotesKey, null);
            DragAndDrop.SetGenericData(DragFolderKey, null);
        }

        private static bool CanDrop(NoteVault vault, string folder)
        {
            if (DragAndDrop.GetGenericData(DragNoteKey) is NoteItem note) return !note.IsExternal;
            if (DragAndDrop.GetGenericData(DragFolderKey) is FolderRef f)
                return f.Vault == vault && !string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                       !folder.StartsWith(f.Folder + "/", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // =====================================================================
        // Expansion state
        // =====================================================================

        private static string Key(NoteVault vault, string folder) => vault.Id + ":" + folder.ToLowerInvariant();

        private void SetExpanded(NoteVault vault, string folder, bool expanded)
        {
            if (vault == null || string.IsNullOrEmpty(folder)) return;
            if (expanded) m_Expanded.Add(Key(vault, folder)); else m_Expanded.Remove(Key(vault, folder));
            SaveExpanded();
        }

        private void SaveExpanded() => EditorPrefs.SetString(ExpandedPrefKey, string.Join("\n", m_Expanded));

        private static string FormatRelativeTime(DateTime dt)
        {
            var span = DateTime.UtcNow - dt;
            if (span.TotalMinutes < 1) return "now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays}d";
            return dt.ToLocalTime().ToString("MMM dd");
        }
    }
}
