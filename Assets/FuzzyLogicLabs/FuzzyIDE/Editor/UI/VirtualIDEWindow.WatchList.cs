using System;
using System.Collections.Generic;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public class EditorTrackedVariableContext
    {
        public string Id;
        public string ScriptPath;
        public int LineNumber;
        public string VariableName;
        public string CurrentValue;
    }

    public partial class VirtualIDEWindow
    {
        private VisualElement m_BottomPanel;
        private VisualElement m_BottomSplitter;
        private ListView m_WatchListView;

        private void InitializeBottomPanel()
        {
            m_BottomSplitter = new VisualElement();
            m_BottomSplitter.style.height = 4;
            m_BottomSplitter.style.backgroundColor = new Color(0.176f, 0.176f, 0.176f);
            m_BottomSplitter.style.cursor = GetResizeCursor(UnityEditor.MouseCursor.SplitResizeUpDown);
            
            bool isDragging = false;
            Vector2 startPointerPos = Vector2.zero;
            float startHeight = 150;

            m_BottomSplitter.RegisterCallback<PointerDownEvent>(e => 
            {
                isDragging = true;
                startPointerPos = e.position;
                startHeight = m_BottomPanel.resolvedStyle.height > 0 ? m_BottomPanel.resolvedStyle.height : 150;
                m_BottomSplitter.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            m_BottomSplitter.RegisterCallback<PointerUpEvent>(e =>
            {
                if (isDragging)
                {
                    isDragging = false;
                    m_BottomSplitter.ReleasePointer(e.pointerId);
                    e.StopPropagation();
                }
            });
            m_BottomSplitter.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (isDragging)
                {
                    float deltaY = startPointerPos.y - e.position.y;
                    float newHeight = startHeight + deltaY;
                    if (newHeight < 50) newHeight = 50;
                    if (rootVisualElement.layout.height > 100 && newHeight > rootVisualElement.layout.height - 100) newHeight = rootVisualElement.layout.height - 100;
                    m_BottomPanel.style.height = newHeight;
                    e.StopPropagation();
                }
            });

            m_BottomPanel = new VisualElement();
            m_BottomPanel.style.height = 150;
            m_BottomPanel.style.minHeight = 0;
            m_BottomPanel.style.backgroundColor = new Color(0.145f, 0.145f, 0.149f);
            m_BottomPanel.style.flexDirection = FlexDirection.Column;
            m_BottomPanel.style.overflow = Overflow.Hidden;
            
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.backgroundColor = new Color(0.176f, 0.176f, 0.176f);
            header.style.height = 25;
            header.style.minHeight = 25;
            header.style.flexShrink = 0;
            header.style.alignItems = Align.Center;
            
            var tabsContainer = new VisualElement();
            tabsContainer.style.flexDirection = FlexDirection.Row;
            tabsContainer.style.flexGrow = 1;
            tabsContainer.style.flexShrink = 0;

            var watchTab = new Button();
            watchTab.text = "LIVE WATCH";
            watchTab.style.unityFontStyleAndWeight = FontStyle.Bold;
            watchTab.style.color = new Color(0.7f, 0.7f, 0.7f);
            watchTab.style.fontSize = 11;
            watchTab.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
            watchTab.style.borderTopWidth = 0; watchTab.style.borderBottomWidth = 0;
            watchTab.style.borderLeftWidth = 0; watchTab.style.borderRightWidth = 0;

            tabsContainer.Add(watchTab);
            
            var popOutBtn = new Button(() => {
                ToggleWatchPanel(false);
                LiveWatchWindow.ShowWindow();
            }) { text = "↗" };
            popOutBtn.tooltip = "Detach Live Watch to a standalone Unity window";
            popOutBtn.style.backgroundColor = Color.clear;
            popOutBtn.style.color = new Color(0.75f, 0.75f, 0.75f);
            popOutBtn.style.borderTopWidth = 0; popOutBtn.style.borderBottomWidth = 0;
            popOutBtn.style.borderLeftWidth = 0; popOutBtn.style.borderRightWidth = 0;
            popOutBtn.style.marginRight = 4;
            popOutBtn.style.fontSize = 14;
            popOutBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            popOutBtn.style.width = 20;
            popOutBtn.style.height = 20;
            popOutBtn.style.paddingLeft = 0; popOutBtn.style.paddingRight = 0;
            popOutBtn.style.paddingTop = 0; popOutBtn.style.paddingBottom = 0;
            ApplyButtonHover(popOutBtn, new Color(0.75f, 0.75f, 0.75f), Color.white, Color.clear, new Color(0.3f, 0.3f, 0.35f, 0.8f));

            var closeBtn = new Button(() => { ToggleWatchPanel(false); }) { text = "X" };
            closeBtn.style.backgroundColor = Color.clear;
            closeBtn.style.color = new Color(0.7f, 0.7f, 0.7f);
            closeBtn.style.borderTopWidth = 0; closeBtn.style.borderBottomWidth = 0;
            closeBtn.style.borderLeftWidth = 0; closeBtn.style.borderRightWidth = 0;
            ApplyButtonHover(closeBtn, new Color(0.7f, 0.7f, 0.7f), Color.white, Color.clear, new Color(0.8f, 0.2f, 0.2f, 0.8f));
            
            header.Add(tabsContainer);
            header.Add(popOutBtn);
            header.Add(closeBtn);
            m_BottomPanel.Add(header);

            m_WatchListView = new ListView();
            m_WatchListView.style.flexGrow = 1;
            m_WatchListView.fixedItemHeight = 25; 
            m_WatchListView.makeItem = MakeWatchItem;
            m_WatchListView.bindItem = BindWatchItem;
            m_WatchListView.itemsSource = new List<EditorTrackedVariableContext>();
            m_WatchListView.selectionType = SelectionType.Single;
            
            m_WatchListView.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.clickCount == 2)
                {
                    if (m_WatchListView.selectedIndex >= 0)
                    {
                        var ctx = (EditorTrackedVariableContext)m_WatchListView.itemsSource[m_WatchListView.selectedIndex];
                        NavigateToWatch(ctx);
                    }
                }
            });

            m_BottomPanel.Add(m_WatchListView);

            rootVisualElement.Add(m_BottomSplitter);
            rootVisualElement.Add(m_BottomPanel);
            
            m_BottomPanel.style.display = m_IsWatchPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;
            m_BottomSplitter.style.display = m_IsWatchPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void ToggleWatchPanel(bool state)
        {
            m_IsWatchPanelOpen = state;
            if (m_BottomPanel != null) m_BottomPanel.style.display = state ? DisplayStyle.Flex : DisplayStyle.None;
            if (m_BottomSplitter != null) m_BottomSplitter.style.display = state ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateToolbarButtonsState();
        }

        private VisualElement MakeWatchItem()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 5;
            row.style.paddingRight = 5;

            var leftBox = new VisualElement();
            leftBox.style.flexDirection = FlexDirection.Row;
            leftBox.style.flexGrow = 1;
            
            var scriptLabel = new Label();
            scriptLabel.name = "scriptLabel";
            scriptLabel.style.width = 150;
            scriptLabel.style.color = new Color(0.3f, 0.6f, 0.9f); // light blue
            
            var varLabel = new Label();
            varLabel.name = "varLabel";
            varLabel.style.width = 150;
            varLabel.style.color = new Color(0.8f, 0.8f, 0.8f);

            var valLabel = new Label();
            valLabel.name = "valLabel";
            valLabel.style.flexGrow = 1;
            valLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valLabel.style.color = new Color(0.8f, 0.9f, 0.5f); // yellowish

            leftBox.Add(scriptLabel);
            leftBox.Add(varLabel);
            leftBox.Add(valLabel);
            
            var unsubBtn = new Button();
            unsubBtn.name = "unsubBtn";
            unsubBtn.text = "X";
            unsubBtn.tooltip = "Unsubscribe & Remove Tracking Code";
            unsubBtn.style.backgroundColor = Color.clear;
            unsubBtn.style.color = Color.red;
            unsubBtn.style.borderTopWidth = 0; unsubBtn.style.borderBottomWidth = 0;
            unsubBtn.style.borderLeftWidth = 0; unsubBtn.style.borderRightWidth = 0;
            ApplyButtonHover(unsubBtn, Color.red, Color.white, Color.clear, new Color(0.8f, 0.2f, 0.2f, 0.8f));

            row.Add(leftBox);
            row.Add(unsubBtn);

            return row;
        }

        private void BindWatchItem(VisualElement element, int index)
        {
            var list = (List<EditorTrackedVariableContext>)m_WatchListView.itemsSource;
            if (index < 0 || index >= list.Count) return;

            var ctx = list[index];
            var scriptLabel = element.Q<Label>("scriptLabel");
            var varLabel = element.Q<Label>("varLabel");
            var valLabel = element.Q<Label>("valLabel");
            var unsubBtn = element.Q<Button>("unsubBtn");

            scriptLabel.text = $"{System.IO.Path.GetFileName(ctx.ScriptPath)} : {ctx.LineNumber}";
            varLabel.text = ctx.VariableName;
            valLabel.text = ctx.CurrentValue;
            
            element.userData = ctx.Id;
            valLabel.userData = ctx.Id;

            unsubBtn.clickable = new Clickable(async () =>
            {
                await RemoveWatchLive(ctx);
            });
        }

        private void RefreshWatchPanelData()
        {
            if (!m_IsWatchPanelOpen || m_WatchListView == null) return;

            var assemblies = UnityCompat.GetLoadedAssemblies();
            System.Type trackerType = null;
            System.Collections.IDictionary stateDict = null;

            foreach (var asm in assemblies)
            {
                var t = asm.GetType("FuzzyLogicLabs.LiveStateTracker");
                if (t != null)
                {
                    var f = t.GetField("State", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (f != null)
                    {
                        var dict = f.GetValue(null) as System.Collections.IDictionary;
                        if (dict != null)
                        {
                            if (dict.Count > 0 || stateDict == null)
                            {
                                trackerType = t;
                                stateDict = dict;
                            }
                        }
                    }
                }
            }

            if (trackerType == null || stateDict == null)
            {
                // Debug.LogWarning("[FuzzyIDE WatchPanel] Could not find FuzzyLogicLabs.LiveStateTracker type or State field.");
                return;
            }

            var list = (List<EditorTrackedVariableContext>)m_WatchListView.itemsSource;
            bool layoutChanged = false;

            bool keysMatch = true;
            if (list.Count != stateDict.Count)
            {
                keysMatch = false;
            }
            else
            {
                foreach (System.Collections.DictionaryEntry kvp in stateDict)
                {
                    if (!list.Exists(x => x.Id == (string)kvp.Key))
                    {
                        keysMatch = false;
                        break;
                    }
                }
            }

            if (!keysMatch)
            {
                // Debug.Log($"[FuzzyIDE WatchPanel] Updating list. Old count={list.Count}, New count={stateDict.Count}");
                list.Clear();
                foreach (System.Collections.DictionaryEntry kvp in stateDict)
                {
                    object val = kvp.Value;
                    var type = val.GetType();
                    
                    try 
                    {
                        list.Add(new EditorTrackedVariableContext
                        {
                            Id = (string)kvp.Key,
                            ScriptPath = (string)type.GetField("ScriptPath").GetValue(val),
                            VariableName = (string)type.GetField("VariableName").GetValue(val),
                            CurrentValue = (string)type.GetField("CurrentValue").GetValue(val)
                        });
                    } 
                    catch (System.Exception ex) 
                    {
                        Debug.LogError($"[FuzzyIDE WatchPanel] Error parsing field: {ex.Message}");
                    }
                }
                layoutChanged = true;
            }
            else
            {
                bool needsRepaint = false;
                m_WatchListView.Query<VisualElement>().ForEach(el =>
                {
                    if (el.userData is string trackId)
                    {
                        if (stateDict.Contains(trackId))
                        {
                            object val = stateDict[trackId];
                            string liveValue = (string)val.GetType().GetField("CurrentValue").GetValue(val);
                            var valLabel = el.Q<Label>("valLabel");
                            if (valLabel != null && valLabel.text != liveValue)
                            {
                                valLabel.text = liveValue;
                                needsRepaint = true;
                            }
                            
                            var scriptLabel = el.Q<Label>("scriptLabel");
                            if (scriptLabel != null)
                            {
                                int dynamicLine = -1;
                                if (m_LeftPane != null && m_LeftPane.Viewer != null)
                                    dynamicLine = m_LeftPane.Viewer.GetDynamicLineForTrackId(trackId);
                                if (dynamicLine == -1 && m_IsRightPaneOpen && m_RightPane != null && m_RightPane.Viewer != null)
                                    dynamicLine = m_RightPane.Viewer.GetDynamicLineForTrackId(trackId);

                                string fileName = System.IO.Path.GetFileName((string)val.GetType().GetField("ScriptPath").GetValue(val));
                                
                                string newScriptLabelText;
                                if (dynamicLine >= 0)
                                {
                                    newScriptLabelText = $"{fileName} : {dynamicLine + 1}";
                                    var ctx = list.Find(x => x.Id == trackId);
                                    if (ctx != null) ctx.LineNumber = dynamicLine + 1;
                                }
                                else
                                {
                                    int underscore = trackId.LastIndexOf('_');
                                    int originalLine = 0;
                                    if (underscore >= 0 && int.TryParse(trackId.Substring(underscore + 1), out originalLine))
                                        newScriptLabelText = $"{fileName} : {originalLine}";
                                    else
                                        newScriptLabelText = $"{fileName} : ?";
                                }

                                if (scriptLabel.text != newScriptLabelText)
                                {
                                    scriptLabel.text = newScriptLabelText;
                                    needsRepaint = true;
                                }
                            }
                        }
                    }
                });
                if (needsRepaint) Repaint();
            }

            if (layoutChanged)
            {
                var currentList = m_WatchListView.itemsSource;
                m_WatchListView.itemsSource = null;
                m_WatchListView.itemsSource = currentList;
                m_WatchListView.RefreshItems();
                Repaint();
            }
        }

        private string ResolveScriptPath(string scriptPath)
        {
            if (m_OpenedFiles.ContainsKey(scriptPath)) return scriptPath;
            foreach (var key in m_OpenedFiles.Keys)
            {
                if (key.EndsWith(scriptPath)) return key;
            }
            if (m_ProjectScripts != null)
            {
                foreach (var path in m_ProjectScripts)
                {
                    if (path.EndsWith(scriptPath)) return path;
                }
            }
            return scriptPath;
        }

        private void NavigateToWatch(EditorTrackedVariableContext ctx)
        {
            string resolvedPath = ResolveScriptPath(ctx.ScriptPath);
            LoadFileAndJumpToLine(resolvedPath, -1);
            
            if (m_ActivePane != null && m_ActivePane.Viewer != null)
            {
                int dynamicLine = m_ActivePane.Viewer.GetDynamicLineForTrackId(ctx.Id);
                if (dynamicLine >= 0)
                {
                    m_ActivePane.Viewer.JumpToLine(dynamicLine);
                }
                else
                {
                    m_ActivePane.Viewer.JumpToLine(ctx.LineNumber - 1);
                }
            }
        }

        private async System.Threading.Tasks.Task RemoveWatchLive(EditorTrackedVariableContext ctx)
        {
            var assemblies = UnityCompat.GetLoadedAssemblies();
            System.Type trackerType = null;
            foreach (var asm in assemblies)
            {
                trackerType = asm.GetType("FuzzyLogicLabs.LiveStateTracker");
                if (trackerType != null) break;
            }

            if (trackerType != null)
            {
                var field = trackerType.GetField("State", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (field != null)
                {
                    var stateDict = field.GetValue(null) as System.Collections.IDictionary;
                    if (stateDict != null)
                    {
                        stateDict.Remove(ctx.Id);
                    }
                }
            }
            
            RefreshWatchPanelData(); 

            // Sync current active tabs to state to ensure latest unsaved edits are passed to Roslyn
            if (m_LeftPane != null && !string.IsNullOrEmpty(m_LeftPane.ActiveTab) && m_OpenedFiles.TryGetValue(m_LeftPane.ActiveTab, out var leftState))
                leftState.Lines = m_LeftPane.Viewer.GetLines();
            if (m_IsRightPaneOpen && m_RightPane != null && !string.IsNullOrEmpty(m_RightPane.ActiveTab) && m_OpenedFiles.TryGetValue(m_RightPane.ActiveTab, out var rightState))
                rightState.Lines = m_RightPane.Viewer.GetLines();

            var allFilesText = new Dictionary<string, string>();
            foreach (var kvp in m_OpenedFiles)
            {
                allFilesText[kvp.Key] = string.Join("\n", kvp.Value.Lines);
            }

            string resolvedPath = ResolveScriptPath(ctx.ScriptPath);
            var change = await RoslynProjectExtensions.RemoveWatchLiveAsync(resolvedPath, ctx.Id, allFilesText);
            if (change != null)
            {
                if (!m_OpenedFiles.TryGetValue(change.FilePath, out var state))
                {
                    state = new FileState { FilePath = change.FilePath };
                    string txt = System.IO.File.Exists(change.FilePath) ? System.IO.File.ReadAllText(change.FilePath) : "";
                    state.Lines = new List<string>(txt.Replace("\r\n", "\n").Split('\n'));
                    m_OpenedFiles[change.FilePath] = state;
                }
                
                string cleanCode = change.NewText.Replace("\r\n", "\n");
                
                var cmds = new List<IEditCommand>();
                
                if (m_LeftPane.OpenedTabs.Contains(change.FilePath) && m_LeftPane.ActiveTab == change.FilePath)
                {
                    var lines = m_LeftPane.Viewer.GetLines();
                    cmds.Add(new DeleteSelectionCommand(lines, 0, 0, lines.Count - 1, lines[lines.Count - 1].Length, (r, c) => { }));
                    cmds.Add(new InsertTextCommand(lines, cleanCode, 0, 0, (r, c) => { }));
                    m_LeftPane.Viewer.ExecuteHistory(cmds);
                }
                else if (m_IsRightPaneOpen && m_RightPane != null && m_RightPane.OpenedTabs.Contains(change.FilePath) && m_RightPane.ActiveTab == change.FilePath)
                {
                    var lines = m_RightPane.Viewer.GetLines();
                    cmds.Add(new DeleteSelectionCommand(lines, 0, 0, lines.Count - 1, lines[lines.Count - 1].Length, (r, c) => { }));
                    cmds.Add(new InsertTextCommand(lines, cleanCode, 0, 0, (r, c) => { }));
                    m_RightPane.Viewer.ExecuteHistory(cmds);
                }
                else
                {
                    cmds.Add(new DeleteSelectionCommand(state.Lines, 0, 0, state.Lines.Count - 1, state.Lines[state.Lines.Count - 1].Length, (r, c) => { }));
                    cmds.Add(new InsertTextCommand(state.Lines, cleanCode, 0, 0, (r, c) => { }));
                    var composite = new CompositeCommand(cmds);
                    composite.Execute();
                    state.History.AddCommand(composite);
                    state.IsDirty = true;
                }
                
                // We no longer force save and compile here.
            }
        }

        private void ApplyButtonHover(Button btn, Color baseColor, Color hoverColor, Color baseBg = default, Color hoverBg = default)
        {
            if (baseBg == default) baseBg = Color.clear;
            if (hoverBg == default) hoverBg = new Color(0.3f, 0.3f, 0.3f, 0.5f);
            
            btn.RegisterCallback<MouseEnterEvent>(e => {
                btn.style.color = hoverColor;
                btn.style.backgroundColor = hoverBg;
            });
            btn.RegisterCallback<MouseLeaveEvent>(e => {
                btn.style.color = baseColor;
                btn.style.backgroundColor = baseBg;
            });
        }
    }
}