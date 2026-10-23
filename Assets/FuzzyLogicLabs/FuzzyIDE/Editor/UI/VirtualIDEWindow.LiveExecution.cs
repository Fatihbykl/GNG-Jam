using System.Collections.Generic;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualIDEWindow
    {
        private async void HandleWatchLiveRequested(EditorPaneState pane, int caretPosition)
        {
            if (pane.ActiveTab == null) return;

            if (m_LeftPane != null && !string.IsNullOrEmpty(m_LeftPane.ActiveTab) && m_OpenedFiles.TryGetValue(m_LeftPane.ActiveTab, out var leftState))
                leftState.Lines = m_LeftPane.Viewer.GetLines();
            if (m_IsRightPaneOpen && m_RightPane != null && !string.IsNullOrEmpty(m_RightPane.ActiveTab) && m_OpenedFiles.TryGetValue(m_RightPane.ActiveTab, out var rightState))
                rightState.Lines = m_RightPane.Viewer.GetLines();

            var allFilesText = new Dictionary<string, string>();
            foreach (var kvp in m_OpenedFiles)
            {
                allFilesText[kvp.Key] = string.Join("\n", kvp.Value.Lines);
            }

            var change = await RoslynProjectExtensions.InjectWatchLiveAsync(pane.ActiveTab, caretPosition, allFilesText);

            if (change != null)
            {
                if (m_OpenedFiles.TryGetValue(change.FilePath, out var state))
                {
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
                    
                    ToggleWatchPanel(true);
                    UpdateWindowTitle();
                    RefreshTabBar(m_LeftPane);
                    RefreshTabBar(m_RightPane);
                    
                    // We no longer force save and compile here. The user will save/compile when ready.
                }
            }
            else
            {
                Debug.LogWarning("[FuzzyIDE] Could not find a valid assignment or statement to inject Watch Live tracker.");
            }
        }

        private async void HandleRemoveWatchLiveRequested(EditorPaneState pane, int caretPosition)
        {
            if (pane == null || pane.ActiveTab == null || pane.Viewer == null) return;

            int line = pane.Viewer.CaretLine;
            string fileName = System.IO.Path.GetFileName(pane.ActiveTab);
            
            if (m_WatchListView != null && m_WatchListView.itemsSource is List<EditorTrackedVariableContext> trackedList)
            {
                var targetCtx = trackedList.Find(x => ResolveScriptPath(x.ScriptPath) == pane.ActiveTab && (x.LineNumber == line + 1 || x.LineNumber == line + 2));
                if (targetCtx != null)
                {
                    await RemoveWatchLive(targetCtx);
                    return;
                }
            }

            var lines = pane.Viewer.GetLines();
            if (line >= 0 && line < lines.Count && lines[line].Contains("LiveStateTracker.Track"))
            {
                var cmds = new List<IEditCommand>();
                int delEndLine = line < lines.Count - 1 ? line + 1 : line;
                int delEndCol = line < lines.Count - 1 ? 0 : lines[line].Length;
                cmds.Add(new DeleteSelectionCommand(lines, line, 0, delEndLine, delEndCol, (r, c) => { }));
                pane.Viewer.ExecuteHistory(cmds);
                RefreshWatchPanelData();
            }
        }
    }
}
