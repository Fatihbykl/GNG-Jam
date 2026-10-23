using System;
using System.Collections.Generic;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualIDEWindow
    {
        private double m_LastShiftTime = 0;
        private VisualElement m_QuickSearchPopup;
        private TextField m_QuickSearchInput;
        private ListView m_QuickSearchList;

        private VisualElement m_ReferencesPanel;
        private ListView m_ReferencesList;
        private List<ReferenceResult> m_ReferencesResults = new List<ReferenceResult>();
        private Label m_ReferencesHeader;

        private VisualElement m_RenamePopup;
        private TextField m_RenameField;
        private int m_RenameCaretPosition;

        private void ApplySettingsPhase14()
        {
            if (m_QuickSearchPopup != null)
            {
                m_QuickSearchPopup.style.backgroundColor = CodeIDESettings.Current.Theme.UIPopupBackground;
                m_QuickSearchPopup.style.borderTopColor = CodeIDESettings.Current.Theme.UIBorder;
                m_QuickSearchPopup.style.borderBottomColor = CodeIDESettings.Current.Theme.UIBorder;
                m_QuickSearchPopup.style.borderLeftColor = CodeIDESettings.Current.Theme.UIBorder;
                m_QuickSearchPopup.style.borderRightColor = CodeIDESettings.Current.Theme.UIBorder;
                m_QuickSearchList.RefreshItems();
            }
            if (m_RenamePopup != null)
            {
                m_RenamePopup.style.backgroundColor = CodeIDESettings.Current.Theme.UIPopupBackground;
                m_RenamePopup.style.borderTopColor = CodeIDESettings.Current.Theme.UIBorder;
                m_RenamePopup.style.borderBottomColor = CodeIDESettings.Current.Theme.UIBorder;
                m_RenamePopup.style.borderLeftColor = CodeIDESettings.Current.Theme.UIBorder;
                m_RenamePopup.style.borderRightColor = CodeIDESettings.Current.Theme.UIBorder;
                
                var title = m_RenamePopup.Q<Label>();
                if (title != null)
                {
                    title.style.color = CodeIDESettings.Current.Theme.DefaultText;
                }
            }
            if (m_RenameField != null)
            {
                m_RenameField.style.backgroundColor = CodeIDESettings.Current.Theme.ActiveLineBackground;
                m_RenameField.style.color = CodeIDESettings.Current.Theme.DefaultText;
                m_RenameField.style.borderTopColor = CodeIDESettings.Current.Theme.SelectionHighlight;
                m_RenameField.style.borderBottomColor = CodeIDESettings.Current.Theme.SelectionHighlight;
                m_RenameField.style.borderLeftColor = CodeIDESettings.Current.Theme.SelectionHighlight;
                m_RenameField.style.borderRightColor = CodeIDESettings.Current.Theme.SelectionHighlight;
            }
            if (m_ReferencesPanel != null)
            {
                m_ReferencesPanel.style.backgroundColor = CodeIDESettings.Current.Theme.Background;
                m_ReferencesPanel.style.borderTopColor = CodeIDESettings.Current.Theme.UIBorder;
                var headerBox = m_ReferencesPanel.Q("refHeaderBox");
                if (headerBox != null) headerBox.style.backgroundColor = CodeIDESettings.Current.Theme.UIPopupBackground;
            }
        }

        private void InitializePhase14UI()
        {
            // Global key event
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnGlobalKeyDownPhase14, TrickleDown.TrickleDown);
            rootVisualElement.RegisterCallback<PointerDownEvent>(OnGlobalPointerDownPhase14, TrickleDown.TrickleDown);

            // Quick Search Popup
            m_QuickSearchPopup = new VisualElement();
            m_QuickSearchPopup.style.position = Position.Absolute;
            m_QuickSearchPopup.style.width = 640;
            m_QuickSearchPopup.style.height = 300;
            m_QuickSearchPopup.style.backgroundColor = CodeIDESettings.Current.Theme.UIPopupBackground;
            m_QuickSearchPopup.style.borderTopWidth = 1; m_QuickSearchPopup.style.borderBottomWidth = 1;
            m_QuickSearchPopup.style.borderLeftWidth = 1; m_QuickSearchPopup.style.borderRightWidth = 1;
            m_QuickSearchPopup.style.borderTopColor = CodeIDESettings.Current.Theme.UIBorder;
            m_QuickSearchPopup.style.borderBottomColor = CodeIDESettings.Current.Theme.UIBorder;
            m_QuickSearchPopup.style.borderLeftColor = CodeIDESettings.Current.Theme.UIBorder;
            m_QuickSearchPopup.style.borderRightColor = CodeIDESettings.Current.Theme.UIBorder;
            m_QuickSearchPopup.style.display = DisplayStyle.None;
            // Center it
            m_QuickSearchPopup.style.left = new StyleLength(new Length(50, LengthUnit.Percent));
            m_QuickSearchPopup.style.top = new StyleLength(new Length(20, LengthUnit.Percent));
            // marginLeft / marginTop to adjust for center
            m_QuickSearchPopup.style.marginLeft = -320;

            m_QuickSearchInput = new TextField();
            m_QuickSearchInput.style.marginTop = 10;
            m_QuickSearchInput.style.marginLeft = 10;
            m_QuickSearchInput.style.marginRight = 10;
            m_QuickSearchInput.style.fontSize = 14;
            m_QuickSearchInput.RegisterValueChangedCallback(evt => UpdateQuickPick(evt.newValue));
            
            m_QuickSearchInput.RegisterCallback<KeyDownEvent>(e => {
                if (e.keyCode == KeyCode.Escape) { 
                    CloseQuickSearch(); 
                    e.StopPropagation(); 
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) {
                    if (m_QuickPickItems.Count > 0)
                    {
                        int idx = m_QuickSearchList.selectedIndex < 0 ? 0 : m_QuickSearchList.selectedIndex;
                        AcceptQuickPick(m_QuickPickItems[idx]);
                    }
                    e.StopPropagation();
                }
                else if (e.keyCode == KeyCode.DownArrow) {
                    if (m_QuickPickItems.Count > 0) {
                        m_QuickSearchList.selectedIndex = Math.Min(m_QuickPickItems.Count - 1, Math.Max(0, m_QuickSearchList.selectedIndex + 1));
                        m_QuickSearchList.ScrollToItem(m_QuickSearchList.selectedIndex);
                    }
                    e.StopPropagation();
                }
                else if (e.keyCode == KeyCode.UpArrow) {
                    if (m_QuickPickItems.Count > 0) {
                        m_QuickSearchList.selectedIndex = Math.Max(0, m_QuickSearchList.selectedIndex - 1);
                        m_QuickSearchList.ScrollToItem(m_QuickSearchList.selectedIndex);
                    }
                    e.StopPropagation();
                }
            }, TrickleDown.TrickleDown);

            m_QuickSearchList = new ListView();
            m_QuickSearchList.style.flexGrow = 1;
            m_QuickSearchList.style.marginTop = 10;
            m_QuickSearchList.makeItem = () => {
                var lbl = new Label { enableRichText = true };
                lbl.style.paddingLeft = 10;
                lbl.style.paddingTop = 5;
                lbl.style.paddingBottom = 5;
                lbl.style.whiteSpace = WhiteSpace.NoWrap;
                lbl.style.overflow = Overflow.Hidden;
                lbl.style.color = CodeIDESettings.Current.Theme.DefaultText;
                return lbl;
            };
            m_QuickSearchList.bindItem = BindQuickPickItem;
            m_QuickSearchList.itemsSource = m_QuickPickItems;
            m_QuickSearchList.selectionType = SelectionType.Single;
            m_QuickSearchList.itemsChosen += items => {
                var selected = items.GetEnumerator();
                if (selected.MoveNext()) AcceptQuickPick(selected.Current as QuickPickItem);
            };
            m_QuickSearchList.RegisterCallback<KeyDownEvent>(e => {
                if (e.keyCode == KeyCode.Escape) { CloseQuickSearch(); e.StopPropagation(); }
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) {
                    if (m_QuickSearchList.selectedItem != null) AcceptQuickPick(m_QuickSearchList.selectedItem as QuickPickItem);
                    e.StopPropagation();
                }
            });

            m_QuickSearchPopup.Add(m_QuickSearchInput);
            m_QuickSearchPopup.Add(m_QuickSearchList);
            rootVisualElement.Add(m_QuickSearchPopup);

            // Rename Popup
            m_RenamePopup = new VisualElement();
            m_RenamePopup.style.position = Position.Absolute;
            m_RenamePopup.style.display = DisplayStyle.None;
            m_RenamePopup.style.backgroundColor = CodeIDESettings.Current.Theme.UIPopupBackground;
            m_RenamePopup.style.borderTopWidth = 1; m_RenamePopup.style.borderBottomWidth = 1;
            m_RenamePopup.style.borderLeftWidth = 1; m_RenamePopup.style.borderRightWidth = 1;
            m_RenamePopup.style.borderTopColor = CodeIDESettings.Current.Theme.UIBorder;
            m_RenamePopup.style.borderBottomColor = CodeIDESettings.Current.Theme.UIBorder;
            m_RenamePopup.style.borderLeftColor = CodeIDESettings.Current.Theme.UIBorder;
            m_RenamePopup.style.borderRightColor = CodeIDESettings.Current.Theme.UIBorder;
            m_RenamePopup.style.paddingLeft = 5; m_RenamePopup.style.paddingRight = 5;
            m_RenamePopup.style.paddingTop = 5; m_RenamePopup.style.paddingBottom = 5;
            m_RenamePopup.style.width = 250;

            var renameTitle = new Label("Semantic Rename:");
            renameTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            renameTitle.style.marginBottom = 5;
            renameTitle.style.color = CodeIDESettings.Current.Theme.DefaultText;
            m_RenamePopup.Add(renameTitle);

            // Rename Field
            m_RenameField = new TextField();
            m_RenameField.style.backgroundColor = CodeIDESettings.Current.Theme.ActiveLineBackground;
            m_RenameField.style.color = CodeIDESettings.Current.Theme.DefaultText;
            m_RenameField.style.borderTopWidth = 1; m_RenameField.style.borderBottomWidth = 1;
            m_RenameField.style.borderLeftWidth = 1; m_RenameField.style.borderRightWidth = 1;
            m_RenameField.style.borderTopColor = CodeIDESettings.Current.Theme.SelectionHighlight;
            m_RenameField.style.borderBottomColor = CodeIDESettings.Current.Theme.SelectionHighlight;
            m_RenameField.style.borderLeftColor = CodeIDESettings.Current.Theme.SelectionHighlight;
            m_RenameField.style.borderRightColor = CodeIDESettings.Current.Theme.SelectionHighlight;
            m_RenameField.RegisterCallback<KeyDownEvent>(OnRenameKeyDown, TrickleDown.TrickleDown);
            m_RenameField.RegisterCallback<FocusOutEvent>(e => m_RenamePopup.style.display = DisplayStyle.None);
            
            m_RenamePopup.Add(m_RenameField);
            rootVisualElement.Add(m_RenamePopup);

            // References Panel
            m_ReferencesPanel = new VisualElement();
            m_ReferencesPanel.style.height = 200;
            m_ReferencesPanel.style.backgroundColor = CodeIDESettings.Current.Theme.Background;
            m_ReferencesPanel.style.borderTopWidth = 2;
            m_ReferencesPanel.style.borderTopColor = CodeIDESettings.Current.Theme.UIBorder;
            m_ReferencesPanel.style.display = DisplayStyle.None;

            var refHeaderBox = new VisualElement { name = "refHeaderBox" };
            refHeaderBox.style.flexDirection = FlexDirection.Row;
            refHeaderBox.style.justifyContent = Justify.SpaceBetween;
            refHeaderBox.style.backgroundColor = CodeIDESettings.Current.Theme.UIPopupBackground;

            m_ReferencesHeader = new Label("References");
            m_ReferencesHeader.style.paddingLeft = 10;
            m_ReferencesHeader.style.paddingTop = 5;
            m_ReferencesHeader.style.paddingBottom = 5;
            m_ReferencesHeader.style.unityFontStyleAndWeight = FontStyle.Bold;

            var closeRefBtn = new Button(() => m_ReferencesPanel.style.display = DisplayStyle.None) { text = "X" };
            closeRefBtn.style.backgroundColor = Color.clear;
            closeRefBtn.style.borderTopWidth = 0; closeRefBtn.style.borderBottomWidth = 0;
            closeRefBtn.style.borderLeftWidth = 0; closeRefBtn.style.borderRightWidth = 0;
            
            refHeaderBox.Add(m_ReferencesHeader);
            refHeaderBox.Add(closeRefBtn);

            m_ReferencesPanel.Add(refHeaderBox);

            m_ReferencesList = new ListView();
            m_ReferencesList.style.flexGrow = 1;
            m_ReferencesList.makeItem = () => new Label();
            m_ReferencesList.bindItem = (e, i) => {
                var lbl = e as Label;
                var res = m_ReferencesResults[i];
                lbl.text = $"{System.IO.Path.GetFileName(res.FilePath)} ({res.LineNumber}, {res.ColumnNumber}): {res.CodeSnippet}";
            };
            m_ReferencesList.itemsSource = m_ReferencesResults;
            m_ReferencesList.selectionType = SelectionType.Single;
            m_ReferencesList.itemsChosen += items => {
                var selected = items.GetEnumerator();
                if (selected.MoveNext()) {
                    var res = (ReferenceResult)selected.Current;
                    LoadFileAndJumpToLine(res.FilePath, res.LineNumber);
                }
            };
            m_ReferencesPanel.Add(m_ReferencesList);

            rootVisualElement.Add(m_ReferencesPanel);

            InitializeFindInFilesUI();
            InitializeProblemsUI();
            InitializeTabSwitcher();
        }

        private void OnGlobalKeyDownPhase14(KeyDownEvent evt)
        {
            if (HandleTabSwitcherKey(evt))
            {
                evt.StopPropagation();
                return;
            }

            if (CodeIDESettings.Current.Keybindings.CheckAction(IDEAction.QuickSearch, evt))
            {
                ShowQuickSearchPopup();
                evt.StopPropagation();
                m_LastShiftTime = 0; // reset
                return;
            }

            if (HandleNavigationShortcut(evt))
            {
                evt.StopPropagation();
                m_LastShiftTime = 0;
                return;
            }

            if (evt.keyCode == KeyCode.LeftShift || evt.keyCode == KeyCode.RightShift)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - m_LastShiftTime < CodeIDESettings.Current.Behaviors.DoubleTapShiftThreshold && now - m_LastShiftTime > 0.05)
                {
                    ShowQuickSearchPopup();
                    evt.StopPropagation();
                    m_LastShiftTime = 0; // reset
                }
                else
                {
                    m_LastShiftTime = now;
                }
            }
        }

        private void OnGlobalPointerDownPhase14(PointerDownEvent evt)
        {
            // Mouse back / forward buttons walk the navigation history like in a browser.
            if (evt.button == 3 || evt.button == 4)
            {
                NavigateHistory(back: evt.button == 3);
                evt.StopPropagation();
                return;
            }

            if (m_QuickSearchPopup != null && m_QuickSearchPopup.style.display == DisplayStyle.Flex)
            {
                var target = evt.target as VisualElement;
                bool isInside = false;
                while (target != null)
                {
                    if (target == m_QuickSearchPopup)
                    {
                        isInside = true;
                        break;
                    }
                    target = target.parent;
                }
                
                if (!isInside)
                {
                    CloseQuickSearch();
                }
            }
        }

        private void ShowQuickSearchPopup()
        {
            ShowQuickPick("");
        }

        private bool HandleNavigationShortcut(KeyDownEvent evt)
        {
            var keys = CodeIDESettings.Current.Keybindings;
            if (keys.CheckAction(IDEAction.FindInFiles, evt)) { ShowFindInFiles(); return true; }
            if (keys.CheckAction(IDEAction.ToggleProblems, evt)) { ToggleProblems(); return true; }
            if (keys.CheckAction(IDEAction.CommandPalette, evt)) { ShowQuickPick(">"); return true; }
            if (keys.CheckAction(IDEAction.GoToSymbol, evt)) { ShowQuickPick("#"); return true; }
            if (keys.CheckAction(IDEAction.GoToFileSymbol, evt)) { ShowQuickPick("@"); return true; }
            if (keys.CheckAction(IDEAction.GoToLine, evt)) { ShowQuickPick(":"); return true; }

            // Text fields keep Alt+arrows for themselves.
            if (!(evt.target is TextField) && !(evt.target is VisualElement ve && ve.GetFirstAncestorOfType<TextField>() != null))
            {
                if (keys.CheckAction(IDEAction.NavigateBack, evt)) { NavigateBack(); return true; }
                if (keys.CheckAction(IDEAction.NavigateForward, evt)) { NavigateForward(); return true; }
            }
            return false;
        }

        private void CloseQuickSearch()
        {
            m_QuickSearchPopup.style.display = DisplayStyle.None;
            if (m_ActivePane != null) m_ActivePane.Viewer.Focus();
        }

        private void HandleRenameRequested(EditorPaneState pane, int caretPos)
        {
            m_RenameCaretPosition = caretPos;
            // Position the rename field
            var viewer = pane.Viewer;
            int line = viewer.CaretLine;
            int col = viewer.CaretColumn;
            
            m_RenamePopup.style.display = DisplayStyle.Flex;
            m_RenameField.value = "";
            
            // Find current word
            int wordStart = col, wordEnd = col;
            if (m_OpenedFiles.TryGetValue(pane.ActiveTab, out var state))
            {
                if (line < state.Lines.Count)
                {
                    string text = state.Lines[line];
                    int start = col - 1;
                    while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] == '_')) start--;
                    start++;
                    int end = col;
                    while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                    if (end > start)
                    {
                        m_RenameField.value = text.Substring(start, end - start);
                        wordStart = start;
                        wordEnd = end;
                    }
                }
            }
            
            // Right below the symbol being renamed (above it near the bottom edge), never on top of it.
            PopupPlacement.Show(m_RenamePopup, viewer.GetTextRangeWorldRect(line, wordStart, wordEnd), preferAbove: false);
            
            m_RenameField.schedule.Execute(() => {
                m_RenameField.Focus();
                m_RenameField.SelectAll();
            }).StartingIn(50);
        }

        private async void OnRenameKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                m_RenamePopup.style.display = DisplayStyle.None;
                if (m_ActivePane != null) m_ActivePane.Viewer.Focus();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                string newName = m_RenameField.value;
                m_RenamePopup.style.display = DisplayStyle.None;
                if (m_ActivePane != null) m_ActivePane.Viewer.Focus();

                var allFilesText = GetFilesTextForRefactoring(m_ActivePane.ActiveTab);
                var changes = await RoslynProjectExtensions.RenameSymbolAsync(m_ActivePane.ActiveTab, m_RenameCaretPosition, newName, allFilesText,
                    CodeIDESettings.Current.Unity.AddFormerlySerializedAsOnRename);
                
                // UnityEngine.Debug.Log($"[FuzzyIDE SemanticRename] CaretPos: {m_RenameCaretPosition}, NewName: {newName}, Changes: {changes.Count}");
                foreach (var change in changes)
                {
                    if (m_OpenedFiles.TryGetValue(change.FilePath, out var state))
                    {
                        var newLines = new List<string>(change.NewText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None));
                        
                        // Use a CompositeEditCommand for undo tracking if we had multiple text changes, 
                        // but since we only have the full new text, let's create a custom replacement command 
                        // or just replace lines directly. We'll replace lines directly for now and trigger UI.
                        // The user asked to "push a CompositeEditCommand to their Undo Stacks". Let's create an InsertTextCommand that just replaces everything, or delete all + insert all.
                        var delCmd = new DeleteSelectionCommand(state.Lines, 0, 0, state.Lines.Count - 1, state.Lines[state.Lines.Count - 1].Length, (l,c) => {});
                        var insCmd = new InsertTextCommand(state.Lines, change.NewText, 0, 0, (l,c) => {});
                        var compCmd = new CompositeCommand(new List<IEditCommand> { delCmd, insCmd });
                        // The active viewer's History is this same object; adding it there too made undo run twice.
                        state.History.AddCommand(compCmd);

                        state.Lines.Clear();
                        state.Lines.AddRange(newLines);
                        state.IsDirty = true;
                        
                        if (m_LeftPane.OpenedTabs.Contains(change.FilePath) && m_LeftPane.ActiveTab == change.FilePath)
                        {
                            m_LeftPane.Viewer.SetLines(state.Lines, false);
                        }
                        if (m_RightPane.OpenedTabs.Contains(change.FilePath) && m_RightPane.ActiveTab == change.FilePath)
                        {
                            m_RightPane.Viewer.SetLines(state.Lines, false);
                        }
                    }
                    else
                    {
                        System.IO.File.WriteAllText(change.FilePath, change.NewText);
                    }
                }

                evt.StopPropagation();
            }
        }

        private async void HandleFindReferencesRequested(EditorPaneState pane, int caretPos)
        {
            m_ReferencesHeader.text = "Searching references...";
            m_ReferencesResults.Clear();
            m_ReferencesList.RefreshItems();
            m_ReferencesPanel.style.display = DisplayStyle.Flex;

            var allFilesText = GetFilesTextForRefactoring(pane.ActiveTab);
            var results = await RoslynProjectExtensions.FindReferencesAsync(pane.ActiveTab, caretPos, allFilesText);
            
            m_ReferencesResults.Clear();
            m_ReferencesResults.AddRange(results);
            m_ReferencesHeader.text = $"References: {results.Count} found";
            m_ReferencesList.RefreshItems();
        }

        // Files of a Unity script assembly are resolved through ProjectCompilationCache, which already knows every
        // file on disk; only the open buffers (possibly unsaved) have to be handed over instead of re-reading the project.
        private Dictionary<string, string> GetFilesTextForRefactoring(string currentPath)
        {
            if (!ProjectCompilationCache.ContainsFile(currentPath)) return GetAllProjectFilesText();

            var dict = new Dictionary<string, string>();
            foreach (var kvp in m_OpenedFiles)
            {
                if (kvp.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    dict[kvp.Key] = string.Join("\n", kvp.Value.Lines);
            }
            return dict;
        }

        private Dictionary<string, string> GetAllProjectFilesText()
        {
            var dict = new Dictionary<string, string>();
            foreach (var path in m_ProjectScripts)
            {
                if (m_OpenedFiles.TryGetValue(path, out var state))
                {
                    dict[path] = string.Join("\n", state.Lines);
                }
                else
                {
                    try
                    {
                        dict[path] = System.IO.File.ReadAllText(path);
                    } catch {}
                }
            }
            return dict;
        }
    }
}
