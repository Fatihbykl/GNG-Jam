using System;
using System.Collections.Generic;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualizedCodeViewer
    {
        private async void ShowContextMenuAsync(int absolutePosition, Vector2 screenPos)
        {
            m_ContextMenuPopup.Clear();
        
            var theme = CodeIDESettings.Current.Theme;

            Action<string, string, Action, List<QuickAction>> addButton = (text, shortcut, onClick, subActions) => {
                var btn = new VisualElement();
                btn.style.backgroundColor = Color.clear;
                btn.style.borderBottomWidth = 0; btn.style.borderTopWidth = 0; btn.style.borderLeftWidth = 0; btn.style.borderRightWidth = 0;
                btn.style.flexDirection = FlexDirection.Row;
                btn.style.justifyContent = Justify.SpaceBetween;
                btn.style.paddingLeft = 8; btn.style.paddingRight = 8;
                btn.style.paddingTop = 4; btn.style.paddingBottom = 4;
                btn.style.minWidth = 220;
            
                var label = new Label(text) { style = { color = theme.DefaultText, fontSize = 12, unityTextAlign = TextAnchor.MiddleLeft, paddingLeft = 0, paddingRight = 0, flexShrink = 0 } };
                btn.Add(label);
            
                if (subActions != null && subActions.Count > 0)
                {
                    var arrowLabel = new Label("▶") { style = { color = new Color(0.5f, 0.5f, 0.5f, 1f), fontSize = 10, marginLeft = 20, unityTextAlign = TextAnchor.MiddleRight, paddingLeft = 0, paddingRight = 0, flexShrink = 0 } };
                    btn.Add(arrowLabel);
                }
                else if (!string.IsNullOrEmpty(shortcut))
                {
                    var shortcutLabel = new Label(shortcut) { style = { color = new Color(0.5f, 0.5f, 0.5f, 1f), fontSize = 12, marginLeft = 20, unityTextAlign = TextAnchor.MiddleRight, paddingLeft = 0, paddingRight = 0, flexShrink = 0 } };
                    btn.Add(shortcutLabel);
                }
            
                if (onClick != null)
                {
                    btn.RegisterCallback<PointerDownEvent>(e => { onClick(); e.StopPropagation(); });
                }
            
                btn.RegisterCallback<PointerEnterEvent>(e => {
                    btn.style.backgroundColor = theme.SelectionHighlight;
                    if (subActions != null && subActions.Count > 0)
                    {
                        m_HideSubmenuTask.Pause();
                        m_SubContextMenuPopup.Clear();
                        m_SubContextMenuPopup.style.backgroundColor = theme.Background;
                        m_SubContextMenuPopup.style.borderTopColor = theme.UIBorder; m_SubContextMenuPopup.style.borderBottomColor = theme.UIBorder; m_SubContextMenuPopup.style.borderLeftColor = theme.UIBorder; m_SubContextMenuPopup.style.borderRightColor = theme.UIBorder;
                    
                        foreach (var subAction in subActions)
                        {
                            var subBtn = new VisualElement();
                            subBtn.style.backgroundColor = Color.clear;
                            subBtn.style.paddingLeft = 8; subBtn.style.paddingRight = 8; subBtn.style.paddingTop = 4; subBtn.style.paddingBottom = 4;
                            subBtn.Add(new Label(subAction.Title) { style = { color = theme.DefaultText, fontSize = 12 } });
                        
                            subBtn.RegisterCallback<PointerDownEvent>(se => { 
                                m_ContextMenuPopup.style.display = DisplayStyle.None;
                                m_SubContextMenuPopup.style.display = DisplayStyle.None;
                                ApplyQuickActionCode(subAction.NewCode); 
                                se.StopPropagation(); 
                            });
                            subBtn.RegisterCallback<PointerEnterEvent>(se => { subBtn.style.backgroundColor = theme.SelectionHighlight; m_HideSubmenuTask.Pause(); });
                            subBtn.RegisterCallback<PointerLeaveEvent>(se => subBtn.style.backgroundColor = Color.clear);
                            m_SubContextMenuPopup.Add(subBtn);
                        }
                    
                        m_SubContextMenuPopup.style.display = DisplayStyle.Flex;
                        m_SubContextMenuPopup.style.left = m_ContextMenuPopup.worldBound.xMax;
                        m_SubContextMenuPopup.style.top = btn.worldBound.yMin;
                    }
                    else
                    {
                        m_HideSubmenuTask.ExecuteLater(150);
                    }
                });
                btn.RegisterCallback<PointerLeaveEvent>(e => {
                    btn.style.backgroundColor = Color.clear;
                    // We don't hide submenu here, we let the submenu stay open until another item is hovered or click happens
                });
            
                m_ContextMenuPopup.Add(btn);
            };
        
            Action addSeparator = () => {
                var sep = new VisualElement();
                sep.style.height = 1; sep.style.backgroundColor = theme.UIBorder; sep.style.marginTop = 4; sep.style.marginBottom = 4;
                m_ContextMenuPopup.Add(sep);
            };

            if (LanguageService is CSharpLanguageService csharpService)
            {
                string fullCode = string.Join("\n", m_Lines);
                var actions = await csharpService.Roslyn.GetQuickActionsAsync(fullCode, absolutePosition);

                if (actions != null && actions.Count > 0)
                {
                    var titleLabel = new Label("Quick Actions") { style = { color = theme.MethodColor, fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, paddingLeft = 8, paddingTop = 2, paddingBottom = 2 } };
                    m_ContextMenuPopup.Add(titleLabel);

                    foreach (var action in actions)
                    {
                        if (action.SubActions != null && action.SubActions.Count > 0)
                        {
                            addButton("💡 " + action.Title, "", null, action.SubActions);
                        }
                        else
                        {
                            addButton("💡 " + action.Title, "Alt+Enter", () => {
                                m_ContextMenuPopup.style.display = DisplayStyle.None;
                                m_SubContextMenuPopup.style.display = DisplayStyle.None;
                                ApplyQuickActionCode(action.NewCode);
                            }, null);
                        }
                    }
                    addSeparator();
                }
            }

            addButton("Cut", "Ctrl+X", () => {
                m_ContextMenuPopup.style.display = DisplayStyle.None;
                m_SubContextMenuPopup.style.display = DisplayStyle.None;
                CutSelection();
            }, null);

            addButton("Copy", "Ctrl+C", () => {
                m_ContextMenuPopup.style.display = DisplayStyle.None;
                m_SubContextMenuPopup.style.display = DisplayStyle.None;
                CopySelection();
            }, null);

            addButton("Paste", "Ctrl+V", () => {
                m_ContextMenuPopup.style.display = DisplayStyle.None;
                m_SubContextMenuPopup.style.display = DisplayStyle.None;
                PasteClipboard();
            }, null);

            if (!string.IsNullOrEmpty(CurrentFilePath))
            {
                addButton(m_HasSelection ? "📝 Add Selection to Note…" : "📝 Link This Line in Note…", "", () => {
                    m_ContextMenuPopup.style.display = DisplayStyle.None;
                    m_SubContextMenuPopup.style.display = DisplayStyle.None;
                    int startLine = m_CaretLine;
                    string selectedText = null;
                    if (m_HasSelection)
                    {
                        GetSelectionRange(out startLine, out _, out _, out _);
                        selectedText = GetSelectedText();
                    }
                    FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration.CodeToNoteCommand.AddCodeSnippetToNotes(CurrentFilePath, startLine + 1, selectedText, m_Lines);
                }, null);

                addButton("🗒 Notes for This File…", "", () => {
                    m_ContextMenuPopup.style.display = DisplayStyle.None;
                    m_SubContextMenuPopup.style.display = DisplayStyle.None;
                    FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration.CodeNotes.ShowNotesForFile(CurrentFilePath, m_CaretLine, m_Lines);
                }, null);
            }
        
            addSeparator();

            Func<IDEAction, string, string> GetActionShortcut = (action, defaultStr) => {
                var bindings = CodeIDESettings.Current.Keybindings.Bindings;
                var binding = bindings.Find(b => b.Action == action)?.Binding;
                return binding != null ? binding.ToString().Replace("Control", "Ctrl") : defaultStr;
            };

            addButton("Select All", "Ctrl+A", () => {
                m_ContextMenuPopup.style.display = DisplayStyle.None;
                m_SubContextMenuPopup.style.display = DisplayStyle.None;
                SelectAll();
            }, null);

            if (LanguageService is CSharpLanguageService csharpServiceForFormat)
            {
                addButton("Auto Format", GetActionShortcut(IDEAction.FormatDocument, "Ctrl+Alt+F"), () => {
                    m_ContextMenuPopup.style.display = DisplayStyle.None;
                    m_SubContextMenuPopup.style.display = DisplayStyle.None;
                    FormatDocumentAsync(csharpServiceForFormat);
                }, null);
                addButton("Code Cleanup", GetActionShortcut(IDEAction.CodeCleanup, "Ctrl+Shift+Alt+F"), () => {
                    m_ContextMenuPopup.style.display = DisplayStyle.None;
                    m_SubContextMenuPopup.style.display = DisplayStyle.None;
                    CleanupCodeAsync();
                }, null);
            }

            addSeparator();

            addButton("Semantic Rename", GetActionShortcut(IDEAction.SemanticRename, "F2"), () => {
                m_ContextMenuPopup.style.display = DisplayStyle.None;
                m_SubContextMenuPopup.style.display = DisplayStyle.None;
                OnRenameRequested?.Invoke(GetAbsoluteCaretPosition());
            }, null);

            addButton("Find All References", GetActionShortcut(IDEAction.FindReferences, "Shift+F12"), () => {
                m_ContextMenuPopup.style.display = DisplayStyle.None;
                m_SubContextMenuPopup.style.display = DisplayStyle.None;
                OnFindReferencesRequested?.Invoke(GetAbsoluteCaretPosition());
            }, null);

            bool isAlreadyWatched = m_CaretLine >= 0 && m_CaretLine < m_Lines.Count && m_Lines[m_CaretLine].Contains("FuzzyLogicLabs.LiveStateTracker.Track");

            if (isAlreadyWatched)
            {
                addButton("Remove from Live Watch", "", () => {
                    m_ContextMenuPopup.style.display = DisplayStyle.None;
                    m_SubContextMenuPopup.style.display = DisplayStyle.None;
                    OnRemoveWatchLiveRequested?.Invoke(GetAbsoluteCaretPosition());
                }, null);
            }
            else
            {
                addButton("Watch Live", "", () => {
                    m_ContextMenuPopup.style.display = DisplayStyle.None;
                    m_SubContextMenuPopup.style.display = DisplayStyle.None;
                    OnWatchLiveRequested?.Invoke(GetAbsoluteCaretPosition());
                }, null);
            }

            m_ContextMenuPopup.style.display = DisplayStyle.Flex;

            m_TargetMenuX = screenPos.x;
            m_TargetMenuY = screenPos.y;
        
            // Initial setup before GeometryChangedEvent triggers the final adjustment
            m_ContextMenuPopup.style.left = m_TargetMenuX;
            m_ContextMenuPopup.style.top = m_TargetMenuY;
        }

        private void ApplyQuickActionCode(string newCode)
        {
            if (!string.IsNullOrEmpty(newCode))
            {
                ApplyFullDocumentReplacement(newCode);
            }
        }

        private async void ApplyUsingDirectiveAsync(CSharpLanguageService csharpService, string ns)
        {
            string fullCode = string.Join("\n", m_Lines);
            string newCode = await csharpService.Roslyn.AddUsingDirectiveAsync(fullCode, ns);

            if (newCode != fullCode && !string.IsNullOrEmpty(newCode))
            {
                ApplyFullDocumentReplacement(newCode);
            }
        }

        private void ApplyFullDocumentReplacement(string newCode)
        {
            int oldLine = m_CaretLine;
            int oldCol = m_CaretColumn;
            var scrollView = m_ListView.Q<ScrollView>();
            Vector2 oldScroll = scrollView != null ? scrollView.scrollOffset : Vector2.zero;

            string cleanCode = newCode.Replace("\r\n", "\n");
            var newLines = cleanCode.Split('\n');
        
            int diffIndex = 0;
            while (diffIndex < m_Lines.Count && diffIndex < newLines.Length && m_Lines[diffIndex] == newLines[diffIndex])
            {
                diffIndex++;
            }

            int lineDelta = newLines.Length - m_Lines.Count;
            bool changedAbove = diffIndex <= oldLine;

            var cmds = new List<IEditCommand>();
            cmds.Add(new DeleteSelectionCommand(m_Lines, 0, 0, m_Lines.Count - 1, m_Lines[m_Lines.Count - 1].Length, SetCaretAction));
            cmds.Add(new InsertTextCommand(m_Lines, cleanCode, 0, 0, SetCaretAction));

            ExecuteHistory(cmds);

            int targetLine = changedAbove ? oldLine + lineDelta : oldLine;
            targetLine = Mathf.Clamp(targetLine, 0, m_Lines.Count - 1);
        
            SetCaretPosition(targetLine, oldCol, true);

            if (scrollView != null)
            {
                if (changedAbove)
                {
                    oldScroll.y += lineDelta * LINE_HEIGHT;
                }
                // Temporarily ignore scroll limits to force exact position
                scrollView.scrollOffset = oldScroll;
                // Execute slightly later to override EnsureCaretVisible's auto-scroll
                schedule.Execute(() => { if (scrollView != null) scrollView.scrollOffset = oldScroll; }).StartingIn(10);
            }
        
            RefreshListView();
        }
    }
}
