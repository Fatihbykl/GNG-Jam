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
    /// Note surface of one workspace pane, laid out like Obsidian: a thin view header (back/forward, breadcrumb,
    /// reading-mode toggle, more menu), the <see cref="MarkdownLiveEditor"/> with the title and properties inside the
    /// page, and a small status bar. Also owns autocomplete, link hover previews, find/replace, the context menu and
    /// conflict handling. Tabs belong to the pane (<see cref="LeafView"/>); outline and backlinks live in the right sidebar.
    /// </summary>
    public class NoteEditorView : VisualElement
    {
        private const float ZoomStep = 0.1f;
        private const int InlineTitleSize = 28;
        private const int WordsPerMinute = 230;

        /// <summary>A view changed the text of a note; other views showing the same note reload it.</summary>
        private static event Action<NoteItem, NoteEditorView> BodyEdited;

        private NoteItem m_CurrentNote;
        private bool m_ReadingMode;

        // View header
        private VisualElement m_HeaderBar;
        private Button m_BackButton;
        private Button m_ForwardButton;
        private VisualElement m_Breadcrumb;
        private Button m_ModeButton;

        // Inside the page
        private TextField m_TitleField;
        private NotePropertiesPanel m_PropertiesPanel;

        private readonly Dictionary<NoteItem, MarkdownLiveEditor.ViewState> m_ViewStates = new Dictionary<NoteItem, MarkdownLiveEditor.ViewState>();

        // Shown when the file changed on disk while this note had unsaved edits
        private VisualElement m_ConflictBanner;
        private VisualElement m_FormattingBar;

        private VisualElement m_EditorHost;
        private MarkdownLiveEditor m_Editor;
        private NoteAutocompletePopup m_AutocompletePopup;
        private TextPos? m_AutocompleteStart;
        private NoteHoverPreview m_HoverPreview;
        private NoteFindBar m_FindBar;
        private IVisualElementScheduledItem m_PendingExternalReload;

        public NoteItem CurrentNote => m_CurrentNote;
        public string SelectedText => m_Editor.GetSelectedText();
        public MarkdownLiveEditor LiveEditor => m_Editor;
        public bool IsReadingMode => m_ReadingMode;

        /// <summary>Raised after a note (or null) was loaded into the view.</summary>
        public event Action<NoteEditorView> NoteLoaded;
        /// <summary>Asks the window to show a right-sidebar pane ("outline", "backlinks").</summary>
        public event Action<string> OnShowSidebarPane;
        /// <summary>Back (-1) / forward (+1) in the pane's history.</summary>
        public event Action<int> OnNavigate;
        /// <summary>Open a note elsewhere (e.g. a link to the right): (note, mode, heading/block subpath or "").</summary>
        public event Action<NoteItem, NoteOpenMode, string> OnOpenNote;
        public event Action<NoteItem> OnRevealInExplorer;
        public event Action OnInsertTemplate;
        public event Action<string> OnExecuteCommand;

        // Status bar
        private VisualElement m_StatusBar;
        private Label m_BacklinksLabel;
        private Label m_WordStatsLabel;
        private Label m_SaveStateLabel;
        private IVisualElementScheduledItem m_PendingStats;

        private bool m_IsLoadingNote;
        private bool m_IsCommittingTitle;

        public NoteEditorView()
        {
            style.flexGrow = 1;
            style.flexDirection = FlexDirection.Column;
            style.backgroundColor = NotesTheme.BgPrimary;

            BuildUI();
            ApplyAppearance();

            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                NoteStore.OnNoteReloaded += HandleNoteReloaded;
                NoteStore.OnNoteConflict += HandleNoteConflict;
                NoteStore.OnNoteSaved += HandleNoteSaved;
                NoteStore.OnNotesChanged += HandleNotesChanged;
                NoteLinkResolver.OnRevealSubpath += HandleRevealSubpath;
                NoteSettings.AppearanceChanged += ApplyAppearance;
                BodyEdited += HandleBodyEdited;
                ApplyAppearance();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                NoteStore.OnNoteReloaded -= HandleNoteReloaded;
                NoteStore.OnNoteConflict -= HandleNoteConflict;
                NoteStore.OnNoteSaved -= HandleNoteSaved;
                NoteStore.OnNotesChanged -= HandleNotesChanged;
                NoteLinkResolver.OnRevealSubpath -= HandleRevealSubpath;
                NoteSettings.AppearanceChanged -= ApplyAppearance;
                BodyEdited -= HandleBodyEdited;
            });
        }

        private void HandleRevealSubpath(NoteItem note, string subpath)
        {
            if (note == m_CurrentNote) RevealSubpath(subpath);
        }

        // =====================================================================
        // Public API (command palette, sidebar, workspace)
        // =====================================================================

        public void OpenFind(bool withReplace = false) => m_FindBar.Open(withReplace);

        public void SetAllFolded(bool folded) => m_Editor.SetAllFolded(folded);

        public void FocusEditor() => m_Editor.FocusEditor();

        /// <summary>Focuses the inline title with its text selected, ready to be typed over (new notes).</summary>
        public void FocusTitle() => schedule.Execute(() =>
        {
            if (m_CurrentNote == null) return;
            if (m_ReadingMode) SetReadingMode(false);
            m_TitleField.Focus();
            m_TitleField.SelectAll();
        });

        /// <summary>Inserts text at the caret (e.g. a template), replacing the selection.</summary>
        public void InsertAtCaret(string text)
        {
            if (m_CurrentNote == null) return;
            m_Editor.InsertText(text);
            m_Editor.FocusEditor();
        }

        /// <summary>Scrolls to a body line of the current note (search results).</summary>
        public void RevealBodyLine(int line)
        {
            if (m_CurrentNote == null || line < 0) return;
            m_Editor.RevealLine(line);
        }

        /// <summary>Scrolls to a heading ("Setup") or block ("^id") of the current note.</summary>
        public void RevealSubpath(string subpath)
        {
            if (string.IsNullOrEmpty(subpath)) return;
            int line = MarkdownScan.FindSubpathLine(m_Editor.Text, subpath);
            if (line >= 0) m_Editor.RevealLine(line);
        }

        /// <summary>Reading view: rendered, read-only, plain clicks open links (Obsidian's Ctrl+E).</summary>
        public void SetReadingMode(bool reading, bool focus = true)
        {
            if (m_ReadingMode == reading) return;
            m_ReadingMode = reading;
            CloseAutocomplete();
            m_Editor.ReadOnly = reading;
            m_TitleField.isReadOnly = reading;
            m_ModeButton.tooltip = reading
                ? $"Switch to editing view ({Shortcut(IDEAction.NoteToggleReadingMode)})"
                : $"Switch to reading view ({Shortcut(IDEAction.NoteToggleReadingMode)})";
            ((NotesIcon)m_ModeButton[0]).SetIcon(reading ? "pencil" : "book-open");
            UpdateFormattingBarVisibility();
            if (focus) m_Editor.FocusEditor();
        }

        public void ToggleReadingMode() => SetReadingMode(!m_ReadingMode);

        /// <summary>Enables the back/forward buttons from the pane's history.</summary>
        public void SetNavigationState(bool canGoBack, bool canGoForward)
        {
            m_BackButton.SetEnabled(canGoBack);
            m_ForwardButton.SetEnabled(canGoForward);
        }

        /// <summary>Focus mode: hides the view header and the status bar.</summary>
        public void SetHeaderHidden(bool hidden)
        {
            m_HeaderBar.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;
            m_StatusBar.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>Changes the editor zoom of every note view by <paramref name="steps"/> (0 resets to 100%).</summary>
        public static void Zoom(int steps)
        {
            float zoom = steps == 0 ? 1f : NoteSettings.EditorZoom + steps * ZoomStep;
            NoteSettings.EditorZoom = Mathf.Clamp(zoom, MarkdownLiveEditor.MinZoom, MarkdownLiveEditor.MaxZoom);
        }

        // =====================================================================
        // Store events
        // =====================================================================

        private void HandleNoteReloaded(NoteItem note)
        {
            if (note == m_CurrentNote) SetNote(note);
            else UpdateBacklinksCount();
        }

        private void HandleNoteConflict(NoteItem note)
        {
            if (note == m_CurrentNote) UpdateConflictBanner();
        }

        private void HandleNoteSaved(NoteItem note)
        {
            if (note != m_CurrentNote) return;
            SetSaveState(SaveState.Saved);
            UpdateBacklinksCount();
        }

        /// <summary>Another pane edited the note shown here: reload it, keeping caret and scroll.</summary>
        private void HandleBodyEdited(NoteItem note, NoteEditorView source)
        {
            if (source == this || note != m_CurrentNote) return;
            m_PendingExternalReload?.Pause();
            m_PendingExternalReload = schedule.Execute(() => { if (m_CurrentNote == note && m_Editor.Text != note.Body) SetNote(note); }).StartingIn(120);
        }

        private void HandleNotesChanged()
        {
            // titles and folders may have changed (rename, move)
            if (m_CurrentNote != null && !m_IsCommittingTitle && m_TitleField.focusController?.focusedElement != m_TitleField)
                m_TitleField.SetValueWithoutNotify(m_CurrentNote.Title ?? "");
            UpdateBreadcrumb();

            // notes appeared/disappeared: link resolution and embeds may have changed
            m_Editor.RefreshRendering();
            UpdateBacklinksCount();
        }

        private void ApplyAppearance()
        {
            float zoom = NoteSettings.EditorZoom;
            m_Editor.SetAppearance(zoom, NoteSettings.ReadableLineLength);
            m_TitleField.style.fontSize = Mathf.RoundToInt(InlineTitleSize * m_Editor.Zoom);
            UpdateTitleVisibility();
            UpdateFormattingBarVisibility();
        }

        // =====================================================================
        // UI construction
        // =====================================================================

        private void BuildUI()
        {
            Clear();

            m_HeaderBar = BuildHeader();
            Add(m_HeaderBar);

            m_ConflictBanner = BuildConflictBanner();
            Add(m_ConflictBanner);

            m_FormattingBar = BuildFormattingBar();
            Add(m_FormattingBar);

            // Editor + popups
            m_EditorHost = new VisualElement { style = { flexGrow = 1, position = Position.Relative } };
            m_Editor = new MarkdownLiveEditor();
            m_Editor.OnTextChanged += () =>
            {
                if (m_IsLoadingNote || m_CurrentNote == null) return;
                SyncToNoteAndSave();
                CheckAutocomplete();
            };
            m_Editor.OnCaretMoved += () =>
            {
                if (m_Editor.HasSelection || m_SelectionShown) ScheduleStats();
                if (m_AutocompletePopup.IsOpen) CheckAutocomplete();
            };
            m_Editor.OnRunCode += (code, lang) => OnExecuteCommand?.Invoke(code);
            m_Editor.OnLinkHover += (target, rect) =>
            {
                if (target == null) { m_HoverPreview.Hide(); return; }
                var p = m_Editor.ChangeCoordinatesTo(m_EditorHost, rect.position);
                m_HoverPreview.Show(target, new Rect(p, rect.size));
            };
            m_Editor.ImagePathResolver = ResolveImage;
            m_Editor.EmbedResolver = ResolveEmbed;
            m_Editor.LinkResolves = target => NoteLinkResolver.IsResolvable(target, m_CurrentNote);
            m_Editor.OnLinkActivated += ActivateLink;
            m_Editor.OnContextMenu += ShowContextMenu;
            m_Editor.OnZoomRequested += Zoom;
            m_Editor.KeyInterceptor = evt => m_AutocompletePopup.IsOpen && m_AutocompletePopup.HandleKeyDown(evt);
            m_EditorHost.Add(m_Editor);
            BuildPageHeader(m_Editor.HeaderSlot);

            m_AutocompletePopup = new NoteAutocompletePopup();
            m_AutocompletePopup.OnCandidateSelected += HandleAutocompleteCandidateSelected;
            m_EditorHost.Add(m_AutocompletePopup);

            m_HoverPreview = new NoteHoverPreview();
            m_EditorHost.Add(m_HoverPreview);

            m_FindBar = new NoteFindBar(m_Editor);
            Add(m_FindBar);

            Add(m_EditorHost);

            m_StatusBar = BuildStatusBar();
            Add(m_StatusBar);
        }

        /// <summary>Back/forward, breadcrumb, reading-mode toggle and the more menu.</summary>
        private VisualElement BuildHeader()
        {
            var header = new VisualElement
            {
                style = { paddingLeft = 6, paddingRight = 6, backgroundColor = NotesTheme.BgPrimary, borderBottomColor = NotesTheme.BorderSubtle }
            }.WithClass("fn-toolbar");

            m_BackButton = NotesIcons.IconButton("arrow-left", $"Navigate back ({Shortcut(IDEAction.NoteNavigateBack)})", () => OnNavigate?.Invoke(-1));
            m_ForwardButton = NotesIcons.IconButton("arrow-right", $"Navigate forward ({Shortcut(IDEAction.NoteNavigateForward)})", () => OnNavigate?.Invoke(1));
            header.Add(m_BackButton);
            header.Add(m_ForwardButton);
            SetNavigationState(false, false);

            m_Breadcrumb = new VisualElement
            {
                style = { flexGrow = 1, flexShrink = 1, flexDirection = FlexDirection.Row, alignItems = Align.Center, justifyContent = Justify.Center, overflow = Overflow.Hidden, marginLeft = 8, marginRight = 8 }
            };
            header.Add(m_Breadcrumb);

            m_ModeButton = NotesIcons.IconButton("book-open", $"Switch to reading view ({Shortcut(IDEAction.NoteToggleReadingMode)})", ToggleReadingMode);
            header.Add(m_ModeButton);
            header.Add(NotesIcons.IconButton("ellipsis-vertical", "More options", ShowMoreMenu));
            return header;
        }

        /// <summary>"vault / folder / Note" — clicking it reveals the note in the file explorer.</summary>
        private void UpdateBreadcrumb()
        {
            m_Breadcrumb.Clear();
            var note = m_CurrentNote;
            if (note == null) return;

            var parts = new List<string>();
            var store = NoteStore.Instance;
            if (note.IsExternal) parts.Add("external");
            else
            {
                if (store.Vaults.Count > 1) parts.Add(store.VaultOf(note)?.Name ?? "");
                if (!string.IsNullOrEmpty(note.Folder)) parts.AddRange(note.Folder.Split('/'));
            }

            void AddPart(string text, bool current)
            {
                var label = new Label(text)
                {
                    tooltip = current ? "Reveal in Files" : null,
                    style =
                    {
                        fontSize = 12, flexShrink = current ? 0 : 1, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis,
                        whiteSpace = WhiteSpace.NoWrap, color = current ? NotesTheme.TextNormal : NotesTheme.TextFaint
                    }
                };
                label.RegisterCallback<ClickEvent>(_ => OnRevealInExplorer?.Invoke(m_CurrentNote));
                label.RegisterCallback<PointerEnterEvent>(_ => label.style.color = NotesTheme.Accent);
                label.RegisterCallback<PointerLeaveEvent>(_ => label.style.color = current ? NotesTheme.TextNormal : NotesTheme.TextFaint);
                m_Breadcrumb.Add(label);
            }

            foreach (var part in parts.Where(p => p.Length > 0))
            {
                AddPart(part, false);
                m_Breadcrumb.Add(new Label("/") { pickingMode = PickingMode.Ignore, style = { fontSize = 12, marginLeft = 4, marginRight = 4, flexShrink = 0, color = NotesTheme.TextFaint } });
            }
            AddPart(note.Title ?? "", true);
        }

        /// <summary>Inline title and properties at the top of the page, scrolling with the text (like Obsidian).</summary>
        private void BuildPageHeader(VisualElement slot)
        {
            m_TitleField = new TextField
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextNormal, marginBottom = 6 }
            }.WithClass("fn-inline-title");
            // Rename only when editing finishes (Enter / focus loss), never per keystroke. Escape reverts.
            m_TitleField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.DownArrow)
                {
                    CommitTitle();
                    m_Editor.FocusEditor();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    m_TitleField.SetValueWithoutNotify(m_CurrentNote?.Title ?? "");
                    m_Editor.FocusEditor();
                    evt.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
            m_TitleField.RegisterCallback<FocusOutEvent>(_ => CommitTitle());
            slot.Add(m_TitleField);

            m_PropertiesPanel = new NotePropertiesPanel();
            m_PropertiesPanel.style.backgroundColor = Color.clear;
            m_PropertiesPanel.style.borderBottomWidth = 0;
            m_PropertiesPanel.style.paddingLeft = 0;
            m_PropertiesPanel.style.paddingRight = 0;
            m_PropertiesPanel.style.marginBottom = 10;
            m_PropertiesPanel.OnChanged += note =>
            {
                NoteStore.Instance.RequestSave(note);
                SetSaveState(SaveState.Unsaved);
            };
            slot.Add(m_PropertiesPanel);
        }

        private void ShowMoreMenu()
        {
            var menu = new GenericMenu();
            var note = m_CurrentNote;
            menu.AddItem(new GUIContent((m_ReadingMode ? "Editing View" : "Reading View") + $"\t{Shortcut(IDEAction.NoteToggleReadingMode)}"), false, ToggleReadingMode);
            if (note != null)
            {
                menu.AddItem(new GUIContent("Open to the Right"), false, () => OnOpenNote?.Invoke(note, NoteOpenMode.SplitRight, ""));
                menu.AddItem(new GUIContent(note.IsPinned ? "Remove Bookmark" : "Bookmark"), false, () => NoteStore.Instance.TogglePin(note));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent($"Find in Note\t{Shortcut(IDEAction.NoteFind)}"), false, () => m_FindBar.Open());
            menu.AddItem(new GUIContent("Find and Replace"), false, () => m_FindBar.Open(withReplace: true));
            menu.AddItem(new GUIContent("Fold All Sections"), false, () => m_Editor.SetAllFolded(true));
            menu.AddItem(new GUIContent("Unfold All Sections"), false, () => m_Editor.SetAllFolded(false));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Show Outline"), false, () => OnShowSidebarPane?.Invoke("outline"));
            menu.AddItem(new GUIContent("Show Backlinks"), false, () => OnShowSidebarPane?.Invoke("backlinks"));
            menu.AddItem(new GUIContent("Appearance/Readable Line Length"), NoteSettings.ReadableLineLength, () => NoteSettings.ReadableLineLength = !NoteSettings.ReadableLineLength);
            menu.AddItem(new GUIContent("Appearance/Formatting Toolbar"), NoteSettings.ShowFormattingBar, () => NoteSettings.ShowFormattingBar = !NoteSettings.ShowFormattingBar);
            menu.AddSeparator("Appearance/");
            menu.AddItem(new GUIContent($"Appearance/Zoom In\t{Shortcut(IDEAction.NoteZoomIn)}"), false, () => Zoom(1));
            menu.AddItem(new GUIContent($"Appearance/Zoom Out\t{Shortcut(IDEAction.NoteZoomOut)}"), false, () => Zoom(-1));
            menu.AddItem(new GUIContent($"Appearance/Reset Zoom ({Mathf.RoundToInt(NoteSettings.EditorZoom * 100)}%)\t{Shortcut(IDEAction.NoteZoomReset)}"), false, () => Zoom(0));
            if (note != null)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Copy Link to Note"), false, () => EditorGUIUtility.systemCopyBuffer = $"[[{note.LinkName}]]");
                if (!note.IsExternal) menu.AddItem(new GUIContent("Reveal in Files"), false, () => OnRevealInExplorer?.Invoke(note));
                menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () => { if (File.Exists(note.FilePath)) EditorUtility.RevealInFinder(note.FilePath); });
                menu.AddItem(new GUIContent("Open as Source in Fuzzy IDE"), false, () => MarkdownAssets.OpenSourceInIde(note));
            }
            menu.ShowAsContext();
        }

        private VisualElement BuildConflictBanner()
        {
            var banner = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    backgroundColor = NotesTheme.WithAlpha(NotesTheme.Warning, 0.18f),
                    borderBottomWidth = 1, borderBottomColor = NotesTheme.WithAlpha(NotesTheme.Warning, 0.5f),
                    paddingLeft = 10, paddingRight = 8, paddingTop = 6, paddingBottom = 6,
                    display = DisplayStyle.None,
                    flexShrink = 0
                }
            };

            banner.Add(NotesIcons.Create("triangle-alert", 16f, NotesTheme.Warning));
            banner.Add(new Label("This note was changed on disk while you had unsaved edits. Autosave is paused.")
            {
                style = { flexGrow = 1, flexShrink = 1, marginLeft = 8, fontSize = 12, color = NotesTheme.TextNormal, whiteSpace = WhiteSpace.Normal }
            });

            Action<string, string, Action<NoteItem>> addBtn = (label, tooltip, onClick) =>
                banner.Add(new Button(() => { if (m_CurrentNote != null) onClick(m_CurrentNote); })
                {
                    text = label,
                    tooltip = tooltip,
                    style = { color = NotesTheme.TextNormal }
                }.WithClass("fn-btn"));

            addBtn("Compare…", "Show both versions side by side", NoteConflictWindow.Open);
            addBtn("Keep Mine", "Overwrite the file on disk with your version", n => NoteStore.Instance.ResolveConflictKeepMine(n));
            addBtn("Use Disk", "Discard your unsaved edits and load the disk version", n => NoteStore.Instance.ResolveConflictUseDisk(n));
            addBtn("Keep Both", "Save your version as a 'conflicted copy' note and load the disk version", n => NoteStore.Instance.ResolveConflictKeepBoth(n));

            return banner;
        }

        /// <summary>Optional toolbar (Appearance ▸ Formatting Toolbar); hidden by default and in reading view.</summary>
        private VisualElement BuildFormattingBar()
        {
            var bar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    alignItems = Align.Center,
                    justifyContent = Justify.Center,
                    flexShrink = 0,
                    backgroundColor = NotesTheme.BgPrimary,
                    borderBottomWidth = 1,
                    borderBottomColor = NotesTheme.BorderSubtle,
                    paddingLeft = 8, paddingRight = 8, paddingTop = 2, paddingBottom = 2
                }
            };

            void AddBtn(string icon, string tooltip, Action onClick) =>
                bar.Add(NotesIcons.IconButton(icon, tooltip, () => { onClick(); m_Editor.FocusEditor(); }));

            void AddGap() => bar.Add(new VisualElement { style = { width = 1, height = 16, marginLeft = 6, marginRight = 6, backgroundColor = NotesTheme.BorderSubtle } });

            AddBtn("heading-1", "Heading 1", () => m_Editor.ApplyLinePrefix("# "));
            AddBtn("heading-2", "Heading 2", () => m_Editor.ApplyLinePrefix("## "));
            AddBtn("heading-3", "Heading 3", () => m_Editor.ApplyLinePrefix("### "));
            AddGap();
            AddBtn("bold", $"Bold ({Shortcut(IDEAction.NoteBold)})", () => m_Editor.WrapSelection("**", "**"));
            AddBtn("italic", $"Italic ({Shortcut(IDEAction.NoteItalic)})", () => m_Editor.WrapSelection("*", "*"));
            AddBtn("strikethrough", "Strikethrough", () => m_Editor.WrapSelection("~~", "~~"));
            AddBtn("code", "Inline code", () => m_Editor.WrapSelection("`", "`"));
            AddBtn("link", $"Wiki link ({Shortcut(IDEAction.NoteInsertLink)})", () => m_Editor.WrapSelection("[[", "]]"));
            AddGap();
            AddBtn("square-check", $"Task ({Shortcut(IDEAction.NoteToggleTask)})", () => m_Editor.ToggleTaskOnSelection());
            AddBtn("list", "Bullet list", () => m_Editor.ApplyLinePrefix("- "));
            AddBtn("list-ordered", "Numbered list", () => m_Editor.ApplyLinePrefix("1. "));
            AddBtn("quote", "Quote", () => m_Editor.ApplyLinePrefix("> "));
            AddBtn("info", "Callout", () => m_Editor.ApplyLinePrefix("> [!note] "));
            AddGap();
            AddBtn("square-code", "C# code block", () => m_Editor.InsertCodeBlock("csharp"));
            AddBtn("terminal", "Runnable shell block", () => m_Editor.InsertCodeBlock("sh"));
            AddBtn("table", "Table", () => m_Editor.InsertBlock(TableTemplate));
            AddBtn("minus", "Divider", () => m_Editor.InsertBlock("---"));

            return bar;
        }

        private const string TableTemplate = "| Header 1 | Header 2 |\n| --- | --- |\n| Item A | Item B |";

        private void UpdateTitleVisibility() =>
            m_TitleField.style.display = m_CurrentNote != null && NoteSettings.ShowInlineTitle ? DisplayStyle.Flex : DisplayStyle.None;

        private void UpdateFormattingBarVisibility() =>
            m_FormattingBar.style.display = NoteSettings.ShowFormattingBar && !m_ReadingMode ? DisplayStyle.Flex : DisplayStyle.None;

        private VisualElement BuildStatusBar()
        {
            var bar = new VisualElement
            {
                style = { backgroundColor = NotesTheme.BgPrimary, borderTopColor = NotesTheme.BorderSubtle, justifyContent = Justify.FlexEnd }
            }.WithClass("fn-statusbar");

            m_BacklinksLabel = new Label("0 backlinks") { tooltip = "Show backlinks", style = { color = NotesTheme.TextFaint } }.WithClass("fn-statusbar-item");
            m_BacklinksLabel.RegisterCallback<ClickEvent>(_ => OnShowSidebarPane?.Invoke("backlinks"));
            m_BacklinksLabel.RegisterCallback<PointerEnterEvent>(_ => m_BacklinksLabel.style.color = NotesTheme.TextNormal);
            m_BacklinksLabel.RegisterCallback<PointerLeaveEvent>(_ => m_BacklinksLabel.style.color = NotesTheme.TextFaint);
            bar.Add(m_BacklinksLabel);

            m_WordStatsLabel = new Label("0 words") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-statusbar-item");
            bar.Add(m_WordStatsLabel);

            m_SaveStateLabel = new Label("●") { style = { fontSize = 9, color = NotesTheme.TextFaint } }.WithClass("fn-statusbar-item");
            bar.Add(m_SaveStateLabel);

            return bar;
        }

        private enum SaveState { Saved, Unsaved, Conflict }

        private void SetSaveState(SaveState state)
        {
            switch (state)
            {
                case SaveState.Saved:
                    m_SaveStateLabel.style.color = NotesTheme.TextFaint;
                    m_SaveStateLabel.tooltip = "Saved";
                    break;
                case SaveState.Unsaved:
                    m_SaveStateLabel.style.color = NotesTheme.Warning;
                    m_SaveStateLabel.tooltip = "Unsaved changes — saved automatically in a moment";
                    break;
                default:
                    m_SaveStateLabel.style.color = NotesTheme.Error;
                    m_SaveStateLabel.tooltip = "The file changed on disk; autosave is paused";
                    break;
            }
        }

        private static string Shortcut(IDEAction action) => WorkspaceView.ShortcutText(action);

        // =====================================================================
        // Note binding
        // =====================================================================

        public void SetNote(NoteItem note)
        {
            // remember where we were in the note we are leaving (or keep it when reloading the same note)
            MarkdownLiveEditor.ViewState? state = null;
            if (m_CurrentNote != null)
            {
                var current = m_Editor.GetViewState();
                m_ViewStates[m_CurrentNote] = current;
                if (note == m_CurrentNote) state = current;
            }
            if (state == null && note != null && m_ViewStates.TryGetValue(note, out var saved)) state = saved;

            m_CurrentNote = note;
            m_IsLoadingNote = true;
            CloseAutocomplete();
            m_HoverPreview.Hide();

            m_PropertiesPanel.Bind(note);
            UpdateBreadcrumb();

            if (note == null)
            {
                m_TitleField.SetValueWithoutNotify("");
                UpdateTitleVisibility();
                m_Editor.SetText("");
                UpdateStats();
                UpdateConflictBanner();
                UpdateBacklinksCount();
                m_IsLoadingNote = false;
                NoteLoaded?.Invoke(this);
                return;
            }

            UpdateTitleVisibility();
            m_TitleField.SetValueWithoutNotify(note.Title ?? "");

            m_Editor.SetText(note.Body ?? "");
            if (state.HasValue) m_Editor.RestoreViewState(state.Value);

            UpdateStats();
            UpdateBacklinksCount();
            m_FindBar.RequestRefresh();

            SetSaveState(note.IsDirty ? SaveState.Unsaved : SaveState.Saved);
            UpdateConflictBanner();

            m_IsLoadingNote = false;
            NoteLoaded?.Invoke(this);
        }

        /// <summary>Forgets the remembered caret/scroll of a note (its tab was closed).</summary>
        public void ForgetViewState(NoteItem note) => m_ViewStates.Remove(note);

        // =====================================================================
        // Editing glue
        // =====================================================================

        private void SyncToNoteAndSave()
        {
            if (m_CurrentNote == null) return;

            m_CurrentNote.SetBody(m_Editor.Text);
            NoteStore.Instance.RequestSave(m_CurrentNote);

            if (!m_CurrentNote.HasConflict) SetSaveState(SaveState.Unsaved);

            ScheduleStats();
            m_FindBar.RequestRefresh();
            BodyEdited?.Invoke(m_CurrentNote, this);
        }

        /// <summary>Wraps the selection (or inserts at the caret); used by drag &amp; drop of links.</summary>
        public void InsertTextAtCursor(string prefix, string suffix)
        {
            if (m_CurrentNote == null) return;
            m_Editor.WrapSelection(prefix, suffix);
            m_Editor.FocusEditor();
        }

        /// <summary>Inserts a block-level line (e.g. an image embed) after the caret line.</summary>
        public void InsertBlock(string markdown)
        {
            if (m_CurrentNote == null) return;
            m_Editor.InsertBlock(markdown);
            m_Editor.FocusEditor();
        }

        private string ResolveImage(string target)
        {
            string noteDir = m_CurrentNote != null && !string.IsNullOrEmpty(m_CurrentNote.FilePath) ? Path.GetDirectoryName(m_CurrentNote.FilePath) : null;
            var vault = NoteStore.Instance.VaultOf(m_CurrentNote);
            // Assets markdown keeps its images next to it (or uses Assets/... paths); searching all of Assets by name would be slow.
            string vaultRoot = vault == null ? NoteStore.Instance.RootDirectory : vault.IsProjectFiles ? noteDir : vault.Root;
            return NoteImageCache.ResolvePath(target, noteDir, vaultRoot);
        }

        /// <summary>Text for ![[Note]] / ![[Note#Heading]] / ![[Note#^block]] embeds.</summary>
        private MarkdownLiveEditor.EmbedContent ResolveEmbed(string target)
        {
            var note = NoteStore.Instance.FindNoteByLink(target, m_CurrentNote);
            if (note == null) return null;

            MarkdownScan.SplitLinkTarget(target, out _, out string subpath, out _);
            var lines = MarkdownScan.NormalizeNewlines(note.Body ?? "").Split('\n');
            int start = 0, end = lines.Length;

            if (subpath.Length > 0)
            {
                start = MarkdownScan.FindSubpathLine(note.Body, subpath);
                if (start < 0) return new MarkdownLiveEditor.EmbedContent { Title = $"{note.Title} › {subpath}", Text = "(section not found)" };

                if (subpath.StartsWith("^")) end = start + 1;
                else
                {
                    int level = lines[start].TakeWhile(c => c == '#').Count();
                    end = start + 1;
                    while (end < lines.Length && !(lines[end].StartsWith("#") && lines[end].TakeWhile(c => c == '#').Count() <= level && lines[end].TrimStart('#').StartsWith(" "))) end++;
                }
            }

            var text = string.Join("\n", lines.Skip(start).Take(end - start)).Trim('\n');
            return new MarkdownLiveEditor.EmbedContent { Title = subpath.Length > 0 ? $"{note.Title} › {subpath}" : note.Title, Text = text };
        }

        /// <summary>
        /// Ctrl+Click on a link (a plain click in reading view): open it, to the right with Alt, or create the note
        /// when it does not exist yet (like Obsidian).
        /// </summary>
        private void ActivateLink(string target, bool toTheSide)
        {
            if (string.IsNullOrWhiteSpace(target)) return;

            if (toTheSide)
            {
                var linked = NoteStore.Instance.FindNoteByLink(target, m_CurrentNote);
                if (linked != null)
                {
                    MarkdownScan.SplitLinkTarget(target, out _, out string subpath, out _);
                    OnOpenNote?.Invoke(linked, NoteOpenMode.SplitRight, subpath);
                    return;
                }
            }

            if (NoteLinkResolver.ResolveAndOpen(target, m_CurrentNote)) return;

            if (NoteImageCache.IsImageFile(target))
            {
                string path = ResolveImage(target);
                if (path != null) EditorUtility.RevealInFinder(path);
                return;
            }

            if (NoteLinkResolver.IsResolvable(target, m_CurrentNote)) return; // code/asset link that could not be opened

            MarkdownScan.SplitLinkTarget(target, out string notePart, out _, out _);
            if (notePart.Length == 0 || notePart.StartsWith("vault:", StringComparison.OrdinalIgnoreCase)) return;

            var vault = NoteStore.Instance.VaultOf(m_CurrentNote) ?? NoteStore.Instance.PrimaryVault;
            string folder = notePart.Contains("/") ? notePart.Substring(0, notePart.LastIndexOf('/')) : m_CurrentNote?.Folder ?? "";
            string name = notePart.Contains("/") ? notePart.Substring(notePart.LastIndexOf('/') + 1) : notePart;
            NoteStore.Instance.CreateNote(name, "", null, folder, vault);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.NoteFind, evt))
            {
                m_FindBar.Open();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Escape && m_FindBar.IsOpen)
            {
                m_FindBar.Close();
                evt.StopPropagation();
            }
        }

        // =====================================================================
        // Context menu
        // =====================================================================

        private void ShowContextMenu(string link)
        {
            var menu = new GenericMenu();
            bool editable = m_CurrentNote != null && !m_ReadingMode;
            bool hasSelection = m_Editor.HasSelection;

            void Item(string path, bool enabled, Action action)
            {
                if (enabled) menu.AddItem(new GUIContent(path), false, () => { action(); m_Editor.FocusEditor(); });
                else menu.AddDisabledItem(new GUIContent(path));
            }

            if (link != null)
            {
                Item("Open Link", true, () => ActivateLink(link, false));
                Item("Open Link to the Right", NoteStore.Instance.FindNoteByLink(link, m_CurrentNote) != null, () => ActivateLink(link, true));
                Item("Copy Link Target", true, () => EditorGUIUtility.systemCopyBuffer = link);
                menu.AddSeparator("");
            }

            Item("Cut", editable && hasSelection, m_Editor.Cut);
            Item("Copy", hasSelection, m_Editor.Copy);
            Item("Paste", editable && !string.IsNullOrEmpty(EditorGUIUtility.systemCopyBuffer), m_Editor.Paste);
            Item("Select All", true, m_Editor.SelectAll);

            if (editable)
            {
                menu.AddSeparator("");
                Item($"Format/Bold\t{Shortcut(IDEAction.NoteBold)}", true, () => m_Editor.WrapSelection("**", "**"));
                Item($"Format/Italic\t{Shortcut(IDEAction.NoteItalic)}", true, () => m_Editor.WrapSelection("*", "*"));
                Item("Format/Strikethrough", true, () => m_Editor.WrapSelection("~~", "~~"));
                Item("Format/Highlight", true, () => m_Editor.WrapSelection("==", "=="));
                Item("Format/Inline Code", true, () => m_Editor.WrapSelection("`", "`"));

                for (int level = 1; level <= 6; level++)
                {
                    string prefix = new string('#', level) + " ";
                    Item($"Paragraph/Heading {level}", true, () => m_Editor.ApplyLinePrefix(prefix));
                }
                Item("Paragraph/Body", true, () => m_Editor.ApplyLinePrefix(""));
                menu.AddSeparator("Paragraph/");
                Item("Paragraph/Bullet List", true, () => m_Editor.ApplyLinePrefix("- "));
                Item("Paragraph/Numbered List", true, () => m_Editor.ApplyLinePrefix("1. "));
                Item($"Paragraph/Task List\t{Shortcut(IDEAction.NoteToggleTask)}", true, m_Editor.ToggleTaskOnSelection);
                Item("Paragraph/Quote", true, () => m_Editor.ApplyLinePrefix("> "));

                Item($"Insert/Wiki Link\t{Shortcut(IDEAction.NoteInsertLink)}", true, () => m_Editor.WrapSelection("[[", "]]"));
                Item("Insert/Markdown Link", true, () => m_Editor.WrapSelection("[", "]()"));
                Item("Insert/Callout", true, () => m_Editor.ApplyLinePrefix("> [!note] "));
                Item("Insert/Table", true, () => m_Editor.InsertBlock(TableTemplate));
                Item("Insert/Code Block", true, () => m_Editor.InsertCodeBlock("csharp"));
                Item("Insert/Runnable Shell Block", true, () => m_Editor.InsertCodeBlock("sh"));
                Item("Insert/Divider", true, () => m_Editor.InsertBlock("---"));
                menu.AddSeparator("Insert/");
                Item("Insert/Template…", OnInsertTemplate != null, () => OnInsertTemplate?.Invoke());

                menu.AddSeparator("");
                Item("Extract Selection to New Note", hasSelection, ExtractSelectionToNewNote);
            }

            menu.AddSeparator("");
            Item($"Find in Note\t{Shortcut(IDEAction.NoteFind)}", m_CurrentNote != null, () => m_FindBar.Open());
            Item((m_ReadingMode ? "Editing View" : "Reading View") + $"\t{Shortcut(IDEAction.NoteToggleReadingMode)}", m_CurrentNote != null, ToggleReadingMode);
            menu.ShowAsContext();
        }

        /// <summary>Moves the selected text into a new note and leaves a link to it behind (Obsidian's note composer).</summary>
        private void ExtractSelectionToNewNote()
        {
            var source = m_CurrentNote;
            string text = m_Editor.GetSelectedText();
            if (source == null || string.IsNullOrWhiteSpace(text)) return;

            var store = NoteStore.Instance;
            string title = UniqueTitle(SuggestTitle(text), source);
            var vault = source.IsExternal ? store.PrimaryVault : store.VaultOf(source) ?? store.PrimaryVault;
            string folder = source.IsExternal ? "" : source.Folder;

            m_Editor.InsertText($"[[{title}]]"); // saved through OnTextChanged before the new note takes over the pane
            store.CreateNote(title, text.Trim('\n'), null, folder, vault);
        }

        private static string SuggestTitle(string text)
        {
            string first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            first = first.TrimStart('#', '>', '-', '*', ' ').Trim();
            var clean = new string(first.Where(c => c != '[' && c != ']' && c != '|' && c != '#' && c != '^' && Path.GetInvalidFileNameChars().All(bad => bad != c)).ToArray()).Trim();
            if (clean.Length > 60) clean = clean.Substring(0, 60).TrimEnd();
            return clean.Length > 0 ? clean : "Untitled";
        }

        private static string UniqueTitle(string title, NoteItem context)
        {
            string candidate = title;
            for (int i = 1; NoteStore.Instance.FindNoteByLink(candidate, context) != null; i++) candidate = $"{title} {i}";
            return candidate;
        }

        // =====================================================================
        // Autocomplete ([[links, #tags, /commands)
        // =====================================================================

        private void CheckAutocomplete()
        {
            if (m_CurrentNote == null || m_ReadingMode || m_Editor.HasSelection) { CloseAutocomplete(); return; }

            var caret = m_Editor.Caret;
            string line = m_Editor.Buffer.GetLine(caret.Line);
            if (line.TrimStart().StartsWith("```") || line.StartsWith("    ")) { CloseAutocomplete(); return; }
            string prefix = line.Substring(0, Math.Min(caret.Col, line.Length));

            var rect = m_Editor.GetCaretRect();
            var pos = m_Editor.ChangeCoordinatesTo(m_EditorHost, new Vector2(rect.x, rect.yMax + 4f));
            float hostW = m_EditorHost.layout.width;
            if (!float.IsNaN(hostW)) pos.x = Mathf.Min(pos.x, hostW - 330f);

            // 1. Wiki link: [[query
            int open = prefix.LastIndexOf("[[", StringComparison.Ordinal);
            if (open >= 0 && prefix.IndexOf("]]", open, StringComparison.Ordinal) < 0)
            {
                m_AutocompleteStart = new TextPos(caret.Line, open);
                m_AutocompletePopup.OpenForWikiLink(prefix.Substring(open + 2), pos);
                return;
            }

            // 2. Tag: #query (at line start or after whitespace, not a heading)
            int hash = prefix.LastIndexOf('#');
            if (hash >= 0 && (hash == 0 || char.IsWhiteSpace(prefix[hash - 1])))
            {
                string query = prefix.Substring(hash + 1);
                if (query.IndexOf(' ') < 0 && query.IndexOf(']') < 0 && query.IndexOf('#') < 0)
                {
                    m_AutocompleteStart = new TextPos(caret.Line, hash);
                    m_AutocompletePopup.OpenForTag(query, pos);
                    return;
                }
            }

            // 3. Slash command: /query (at line start or after whitespace)
            int slash = prefix.LastIndexOf('/');
            if (slash >= 0 && (slash == 0 || char.IsWhiteSpace(prefix[slash - 1])))
            {
                string query = prefix.Substring(slash + 1);
                if (query.IndexOf(' ') < 0 && query.Length <= 20)
                {
                    m_AutocompleteStart = new TextPos(caret.Line, slash);
                    m_AutocompletePopup.OpenForSlash(query, pos);
                    if (m_AutocompletePopup.IsOpen) return;
                }
            }

            CloseAutocomplete();
        }

        private void CloseAutocomplete()
        {
            m_AutocompleteStart = null;
            if (m_AutocompletePopup.IsOpen) m_AutocompletePopup.Close();
        }

        private void HandleAutocompleteCandidateSelected(AutocompleteCandidate cand)
        {
            if (cand == null || m_AutocompleteStart == null) return;

            var start = m_AutocompleteStart.Value;
            var caret = m_Editor.Caret;
            m_AutocompleteStart = null;
            if (start.Line != caret.Line || start.Col > caret.Col) return;

            string line = m_Editor.Buffer.GetLine(caret.Line);
            int endCol = caret.Col;
            if (m_AutocompletePopup.Mode == AutocompleteMode.WikiLink && string.CompareOrdinal(line, endCol, "]]", 0, 2) == 0) endCol += 2;

            string text = cand.InsertText ?? "";
            int marker = text.IndexOf("$|", StringComparison.Ordinal);
            if (marker >= 0) text = text.Remove(marker, 2);

            m_Editor.ReplaceRange(start, new TextPos(caret.Line, endCol), text);
            if (marker >= 0) m_Editor.SetCaret(NoteTextBuffer.EndOfInsert(start, text.Substring(0, marker)));
            m_Editor.FocusEditor();
        }

        // =====================================================================
        // Title, conflict
        // =====================================================================

        private void CommitTitle()
        {
            // Guard: the rename dialog steals focus, which raises FocusOut while we are still committing.
            if (m_IsLoadingNote || m_IsCommittingTitle || m_CurrentNote == null || m_ReadingMode) return;

            m_IsCommittingTitle = true;
            try
            {
                var note = m_CurrentNote;
                string newTitle = m_TitleField.value?.Trim();
                if (string.IsNullOrEmpty(newTitle) || newTitle == note.Title || !NoteActions.Rename(note, newTitle))
                {
                    m_TitleField.SetValueWithoutNotify(note.Title ?? "");
                }
            }
            finally
            {
                m_IsCommittingTitle = false;
            }
        }

        private void UpdateConflictBanner()
        {
            bool show = m_CurrentNote != null && m_CurrentNote.HasConflict;
            m_ConflictBanner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show) SetSaveState(SaveState.Conflict);
        }

        // =====================================================================
        // Status bar
        // =====================================================================

        private bool m_SelectionShown;

        private void ScheduleStats()
        {
            m_PendingStats?.Pause();
            m_PendingStats = schedule.Execute(UpdateStats).StartingIn(250);
        }

        private void UpdateStats()
        {
            var buf = m_Editor.Buffer;
            int charCount = 0, wordCount = 0;
            for (int i = 0; i < buf.LineCount; i++)
            {
                string txt = buf.GetLine(i);
                charCount += txt.Length;
                wordCount += CountWords(txt);
            }

            m_SelectionShown = m_Editor.HasSelection;
            if (m_SelectionShown)
            {
                string selected = m_Editor.GetSelectedText();
                m_WordStatsLabel.text = $"{CountWords(selected)} of {wordCount} words · {selected.Length} characters selected";
                return;
            }

            int minutes = Mathf.Max(1, Mathf.RoundToInt(wordCount / (float)WordsPerMinute));
            m_WordStatsLabel.text = $"{wordCount} {(wordCount == 1 ? "word" : "words")} · {charCount} characters · {minutes} min read";
        }

        private static int CountWords(string text)
        {
            int count = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                bool ws = char.IsWhiteSpace(c);
                if (!ws && !inWord) count++;
                inWord = !ws;
            }
            return count;
        }

        private void UpdateBacklinksCount()
        {
            int count = m_CurrentNote == null ? 0 : NoteStore.Instance.Index.GetBacklinkPaths(m_CurrentNote).Count;
            m_BacklinksLabel.text = count == 1 ? "1 backlink" : $"{count} backlinks";
        }
    }
}
