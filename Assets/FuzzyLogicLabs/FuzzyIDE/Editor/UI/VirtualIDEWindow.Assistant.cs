using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.AI;
using System.Linq;
using System;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualIDEWindow
    {
        [SerializeField] private bool m_IsAssistantPanelOpen = false;
        
        private VisualElement m_AssistantPanelContainer;
        private VisualElement m_AssistantSplitter;
        private AssistantView m_AssistantView;

        private void InitializeAssistantPanel(VisualElement mainContent)
        {
            m_AssistantSplitter = new VisualElement();
            m_AssistantSplitter.style.width = 4;
            m_AssistantSplitter.style.backgroundColor = new Color(0.176f, 0.176f, 0.176f);
            m_AssistantSplitter.style.cursor = GetResizeCursor(UnityEditor.MouseCursor.SplitResizeLeftRight);
            
            bool isDragging = false;
            m_AssistantSplitter.RegisterCallback<PointerDownEvent>(e => 
            {
                isDragging = true;
                m_AssistantSplitter.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            m_AssistantSplitter.RegisterCallback<PointerUpEvent>(e =>
            {
                if (isDragging)
                {
                    isDragging = false;
                    m_AssistantSplitter.ReleasePointer(e.pointerId);
                    e.StopPropagation();
                }
            });
            m_AssistantSplitter.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (isDragging)
                {
                    float newWidth = rootVisualElement.worldBound.width - e.position.x;
                    if (newWidth < 250) newWidth = 250;
                    if (newWidth > 800) newWidth = 800;
                    m_AssistantPanelContainer.style.width = newWidth;
                    e.StopPropagation();
                }
            });

            m_AssistantPanelContainer = new VisualElement();
            m_AssistantPanelContainer.style.width = 350;
            m_AssistantPanelContainer.style.minWidth = 250;
            m_AssistantPanelContainer.style.backgroundColor = new Color(0.145f, 0.145f, 0.149f);
            m_AssistantPanelContainer.style.flexDirection = FlexDirection.Column;
            
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.backgroundColor = new Color(0.176f, 0.176f, 0.176f);
            header.style.height = 25;
            header.style.minHeight = 25;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.paddingLeft = 10;
            
            var title = new Label("AI Assistant");
            title.style.color = new Color(0.8f, 0.8f, 0.8f);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(title);
            
            var headerActions = new VisualElement();
            headerActions.style.flexDirection = FlexDirection.Row;
            headerActions.style.alignItems = Align.Center;

            var popOutBtn = new Button(() => {
                ToggleAssistantPanel(false);
                AIAssistantWindow.ShowWindow();
            }) { text = "↗" };
            popOutBtn.tooltip = "Detach AI Assistant to a standalone Unity window";
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

            popOutBtn.RegisterCallback<MouseEnterEvent>(e => {
                popOutBtn.style.color = Color.white;
                popOutBtn.style.backgroundColor = new Color(0.3f, 0.3f, 0.35f, 0.8f);
            });
            popOutBtn.RegisterCallback<MouseLeaveEvent>(e => {
                popOutBtn.style.color = new Color(0.75f, 0.75f, 0.75f);
                popOutBtn.style.backgroundColor = Color.clear;
            });

            var closeBtn = new Button(() => ToggleAssistantPanel(false)) { text = "X" };
            closeBtn.style.backgroundColor = Color.clear;
            closeBtn.style.color = new Color(0.7f, 0.7f, 0.7f);
            closeBtn.style.borderTopWidth = 0; closeBtn.style.borderBottomWidth = 0;
            closeBtn.style.borderLeftWidth = 0; closeBtn.style.borderRightWidth = 0;
            
            closeBtn.RegisterCallback<MouseEnterEvent>(e => {
                closeBtn.style.color = Color.white;
                closeBtn.style.backgroundColor = new Color(0.8f, 0.2f, 0.2f, 0.8f);
            });
            closeBtn.RegisterCallback<MouseLeaveEvent>(e => {
                closeBtn.style.color = new Color(0.7f, 0.7f, 0.7f);
                closeBtn.style.backgroundColor = Color.clear;
            });
            
            headerActions.Add(popOutBtn);
            headerActions.Add(closeBtn);
            header.Add(headerActions);

            m_AssistantPanelContainer.Add(header);
            
            m_AssistantView = new AssistantView();
            m_AssistantView.OnApplyCodeRequested += HandleAssistantApplyCode;
            m_AssistantView.OnApplyDiffRequested += HandleAssistantApplyDiff;
            m_AssistantView.OnCreateFileRequested += HandleAssistantCreateFile;
            m_AssistantView.OnGetContextRequested += HandleAssistantGetContext;
            
            Action<string, string> fixHandler = (error, lineText) => {
                if (AIAssistantWindow.IsOpen)
                {
                    AIAssistantWindow.Instance.SendFixError(error, lineText);
                    AIAssistantWindow.Instance.Focus();
                }
                else
                {
                    if (!m_IsAssistantPanelOpen) ToggleAssistantPanel(true);
                    m_AssistantView.SendDirectRequest($"I have a compilation error:\n{error}\n\nOn this line:\n{lineText}\n\nPlease fix it.");
                }
            };
            
            if (m_LeftPane != null && m_LeftPane.Viewer != null)
                m_LeftPane.Viewer.OnFixErrorRequested += fixHandler;
            if (m_RightPane != null && m_RightPane.Viewer != null)
                m_RightPane.Viewer.OnFixErrorRequested += fixHandler;
            
            m_AssistantPanelContainer.Add(m_AssistantView);

            mainContent.Add(m_AssistantSplitter);
            mainContent.Add(m_AssistantPanelContainer);

            ToggleAssistantPanel(m_IsAssistantPanelOpen);
        }

        public void ToggleAssistantPanel(bool state)
        {
            m_IsAssistantPanelOpen = state;
            if (m_AssistantPanelContainer != null) m_AssistantPanelContainer.style.display = state ? DisplayStyle.Flex : DisplayStyle.None;
            if (m_AssistantSplitter != null) m_AssistantSplitter.style.display = state ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateToolbarButtonsState();
        }

        public void HandleAssistantCreateFile(string path, string code)
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }
                
                System.IO.File.WriteAllText(path, code);
                UnityEditor.AssetDatabase.ImportAsset(path);
                LoadFileAndJumpToLine(path, 0);
                // Debug.Log($"[FuzzyIDE Assistant] Created and opened file: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FuzzyIDE Assistant] Failed to create file {path}: {ex.Message}");
            }
        }

        public void HandleAssistantApplyCode(string code)
        {
            if (m_ActivePane != null && m_ActivePane.Viewer != null)
            {
                var lines = m_ActivePane.Viewer.GetLines();
                
                // For simplicity, just insert at caret or replace selection
                // Since Viewer exposes a way to execute history commands, let's just insert it at caret
                int cLine = m_ActivePane.Viewer.CaretLine;
                int cCol = m_ActivePane.Viewer.CaretColumn;
                
                string[] codeLines = code.Replace("\r\n", "\n").Split('\n');
                var cmds = new System.Collections.Generic.List<FuzzyLogicLabs.FuzzyIDE.Editor.Core.IEditCommand>();
                cmds.Add(new FuzzyLogicLabs.FuzzyIDE.Editor.Core.InsertTextCommand(
                    lines, 
                    code, 
                    cLine, 
                    cCol, 
                    (r, c) => { m_ActivePane.Viewer.SetCaretPosition(r, c, true); }
                ));
                
                m_ActivePane.Viewer.ExecuteHistory(cmds);
                HandleTextChanged(m_ActivePane);
                
                // Debug.Log("[FuzzyIDE Assistant] Code applied to active editor.");
            }
        }

        public void HandleAssistantApplyDiff(string path, string searchCode, string newCode, System.Action onAccept, System.Action onReject)
        {
            if (!string.IsNullOrEmpty(path))
            {
                LoadFileAndJumpToLine(path, -1);
            }
            
            if (m_ActivePane == null || m_ActivePane.Viewer == null || string.IsNullOrEmpty(m_ActivePane.ActiveTab)) return;
            
            var lines = m_ActivePane.Viewer.GetLines();
            
            var searchLinesList = searchCode.Replace("\r\n", "\n").Split('\n').ToList();
            searchLinesList.RemoveAll(l => l.Trim().StartsWith("```"));
            while (searchLinesList.Count > 0 && string.IsNullOrWhiteSpace(searchLinesList[0])) searchLinesList.RemoveAt(0);
            while (searchLinesList.Count > 0 && string.IsNullOrWhiteSpace(searchLinesList[searchLinesList.Count - 1])) searchLinesList.RemoveAt(searchLinesList.Count - 1);
            string[] searchLines = searchLinesList.ToArray();

            var newLinesList = newCode.Replace("\r\n", "\n").Split('\n').ToList();
            newLinesList.RemoveAll(l => l.Trim().StartsWith("```"));
            while (newLinesList.Count > 0 && string.IsNullOrWhiteSpace(newLinesList[0])) newLinesList.RemoveAt(0);
            while (newLinesList.Count > 0 && string.IsNullOrWhiteSpace(newLinesList[newLinesList.Count - 1])) newLinesList.RemoveAt(newLinesList.Count - 1);
            string cleanedNewCode = string.Join("\n", newLinesList);
            
            if (searchLines.Length == 0) return;
            
            int matchStartIndex = -1;
            
            // Simple multi-line matching
            for (int i = 0; i <= lines.Count - searchLines.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < searchLines.Length; j++)
                {
                    if (lines[i + j].Trim() != searchLines[j].Trim())
                    {
                        match = false;
                        break;
                    }
                }
                
                if (match)
                {
                    matchStartIndex = i;
                    break;
                }
            }
            
            if (matchStartIndex != -1)
            {
                var cmds = new System.Collections.Generic.List<FuzzyLogicLabs.FuzzyIDE.Editor.Core.IEditCommand>();
                int matchEndIndex = matchStartIndex + searchLines.Length - 1;
                
                Action<int, int> setCaret = (r, c) => { m_ActivePane.Viewer.SetCaretPosition(r, c, true); };
                cmds.Add(new FuzzyLogicLabs.FuzzyIDE.Editor.Core.DeleteSelectionCommand(
                    lines, 
                    matchStartIndex, 0, 
                    matchEndIndex, lines[matchEndIndex].Length, 
                    setCaret
                ));
                
                cmds.Add(new FuzzyLogicLabs.FuzzyIDE.Editor.Core.InsertTextCommand(
                    lines, 
                    cleanedNewCode, 
                    matchStartIndex, 0, 
                    setCaret
                ));
                
                if (FuzzyLogicLabs.FuzzyIDE.Editor.Core.CodeIDESettings.Current.AI.AutoApplyDiffs)
                {
                    m_ActivePane.Viewer.ExecuteHistory(cmds);
                    HandleTextChanged(m_ActivePane);
                    // Debug.Log("[FuzzyIDE Assistant] Diff applied to active editor.");
                    LoadFileAndJumpToLine(path, matchStartIndex + 1);
                    onAccept?.Invoke();
                }
                else
                {
                    var deletedLines = lines.GetRange(matchStartIndex, searchLines.Length);
                    m_ActivePane.Viewer.ShowDiffPreview(cmds, matchStartIndex, deletedLines, newLinesList.Count, onAccept, onReject);
                    LoadFileAndJumpToLine(path, matchStartIndex + 1);
                }
            }
            else
            {
                Debug.LogWarning("[FuzzyIDE Assistant] Could not find the exact code block to replace. The file might have been changed.");
                onReject?.Invoke();
            }
        }

        public string HandleAssistantGetContext()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            void AppendTab(string tabPath, string paneName)
            {
                if (!string.IsNullOrEmpty(tabPath) && m_OpenedFiles.TryGetValue(tabPath, out var state))
                {
                    string fileContent = string.Join("\n", state.Lines);
                    sb.AppendLine($"<file path=\"{tabPath}\" pane=\"{paneName}\" caretLine=\"{state.CaretLine + 1}\">");
                    sb.AppendLine(fileContent);
                    sb.AppendLine("</file>");
                }
            }

            if (m_LeftPane != null && !string.IsNullOrEmpty(m_LeftPane.ActiveTab))
                AppendTab(m_LeftPane.ActiveTab, "PrimaryPane");
            
            if (m_IsRightPaneOpen && m_RightPane != null && !string.IsNullOrEmpty(m_RightPane.ActiveTab) && m_RightPane.ActiveTab != m_LeftPane?.ActiveTab)
                AppendTab(m_RightPane.ActiveTab, "SecondaryPane");

            string ctx = sb.ToString();
            if (!string.IsNullOrEmpty(ctx))
            {
                ctx = "Currently Visible Files in Workspace:\n" + ctx;
            }
            return ctx;
        }
    }
}
