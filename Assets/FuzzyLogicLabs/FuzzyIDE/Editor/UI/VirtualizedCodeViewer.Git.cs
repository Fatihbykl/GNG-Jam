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
        // -----------------------
        // Git In-Editor Live Diff
        // -----------------------
        public void ScheduleGitDiff(bool immediate = false)
        {
            if (!CodeIDESettings.Current.Git.EnableGitIntegration)
            {
                m_GitLineDiffResult = null;
                m_ListView?.RefreshItems();
                m_Minimap?.MarkDirtyRepaint();
                return;
            }

            if (string.IsNullOrEmpty(CurrentFilePath))
            {
                m_GitLineDiffResult = null;
                return;
            }

            if (immediate)
            {
                _ = RefreshGitDiffAsync();
            }
            else
            {
                m_GitDebounceTask?.Pause();
                m_GitDebounceTask = schedule.Execute(() =>
                {
                    _ = RefreshGitDiffAsync();
                }).StartingIn(250);
            }
        }

        private async System.Threading.Tasks.Task RefreshGitDiffAsync()
        {
            string path = CurrentFilePath;
            if (string.IsNullOrEmpty(path)) return;

            string repoRoot = Git.GitService.RepositoryRoot;
            if (string.IsNullOrEmpty(repoRoot) || !path.Replace('\\', '/').StartsWith(repoRoot.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string relativePath = path.Substring(repoRoot.Length).TrimStart('/', '\\').Replace('\\', '/');

            // If file path changed or HEAD cache is null, fetch HEAD content
            if (m_HeadFileLines == null || m_LastGitFilePath != path)
            {
                m_LastGitFilePath = path;
                string headContent = await Git.GitService.GetHeadFileContentAsync(relativePath);
                if (headContent != null)
                {
                    m_HeadFileLines = new List<string>(headContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None));
                }
                else
                {
                    m_HeadFileLines = null;
                }
            }

            // Copy current lines reference safely
            var currentLines = m_Lines != null ? new List<string>(m_Lines) : null;
            var headLines = m_HeadFileLines;

            var diffResult = await System.Threading.Tasks.Task.Run(() =>
            {
                return Git.GitDiffEngine.ComputeDiff(headLines, currentLines);
            });

            if (CurrentFilePath == path)
            {
                m_GitLineDiffResult = diffResult;
                m_ListView?.RefreshItems();
                m_Minimap?.MarkDirtyRepaint();
                RefreshScrollMarkers();
            }
        }

        private void OnGitStatusUpdated(Git.GitRepositoryStatus status)
        {
            m_HeadFileLines = null;
            ScheduleGitDiff(true);
        }

        private void OnGitMarkerPointerDown(PointerDownEvent evt)
        {
            if (evt.currentTarget is VisualElement marker && marker.userData is int lineIndex)
            {
                ShowGitDiffPeek(lineIndex, evt.position);
                evt.StopPropagation();
            }
        }

        private void ShowGitDiffPeek(int lineIndex, Vector2 screenPos)
        {
            if (m_GitLineDiffResult == null || !m_GitLineDiffResult.LineStatuses.TryGetValue(lineIndex, out var diffType))
                return;

            if (m_GitDiffPopup == null)
            {
                m_GitDiffPopup = new VisualElement();
                m_GitDiffPopup.name = "git-diff-peek-popup";
                m_GitDiffPopup.style.position = Position.Absolute;
                m_GitDiffPopup.style.backgroundColor = new Color(0.18f, 0.18f, 0.2f, 0.98f);
                m_GitDiffPopup.style.borderTopWidth = 1;
                m_GitDiffPopup.style.borderBottomWidth = 1;
                m_GitDiffPopup.style.borderLeftWidth = 1;
                m_GitDiffPopup.style.borderRightWidth = 1;
                m_GitDiffPopup.style.borderTopColor = new Color(0.3f, 0.3f, 0.35f);
                m_GitDiffPopup.style.borderBottomColor = new Color(0.3f, 0.3f, 0.35f);
                m_GitDiffPopup.style.borderLeftColor = new Color(0.3f, 0.3f, 0.35f);
                m_GitDiffPopup.style.borderRightColor = new Color(0.3f, 0.3f, 0.35f);
                m_GitDiffPopup.style.borderTopLeftRadius = 4;
                m_GitDiffPopup.style.borderTopRightRadius = 4;
                m_GitDiffPopup.style.borderBottomLeftRadius = 4;
                m_GitDiffPopup.style.borderBottomRightRadius = 4;
                m_GitDiffPopup.style.paddingLeft = 8;
                m_GitDiffPopup.style.paddingRight = 8;
                m_GitDiffPopup.style.paddingTop = 6;
                m_GitDiffPopup.style.paddingBottom = 6;
                m_GitDiffPopup.style.minWidth = 260;
                m_GitDiffPopup.style.maxWidth = 450;

                if (parent != null)
                {
                    var overlay = parent.Q("popup-overlay-container") ?? this;
                    overlay.Add(m_GitDiffPopup);
                }
                else
                {
                    Add(m_GitDiffPopup);
                }
            }

            m_GitDiffPopup.Clear();

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 4;

            string titleText = diffType == Git.GitLineDiffType.Added ? $"Line {lineIndex + 1}: Added" :
                              (diffType == Git.GitLineDiffType.Modified ? $"Line {lineIndex + 1}: Modified" : $"Line {lineIndex + 1}: Deleted Above");

            var titleLabel = new Label(titleText);
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 11;
            titleLabel.style.color = diffType == Git.GitLineDiffType.Added ? CodeIDESettings.Current.Theme.GitAdded :
                                    (diffType == Git.GitLineDiffType.Modified ? CodeIDESettings.Current.Theme.GitModified : CodeIDESettings.Current.Theme.GitDeleted);
            header.Add(titleLabel);

            var closeBtn = new Button(() => m_GitDiffPopup.style.display = DisplayStyle.None) { text = "✕" };
            closeBtn.style.fontSize = 10;
            closeBtn.style.paddingLeft = 4;
            closeBtn.style.paddingRight = 4;
            closeBtn.style.paddingTop = 1;
            closeBtn.style.paddingBottom = 1;
            closeBtn.style.backgroundColor = Color.clear;
            closeBtn.style.borderTopWidth = 0;
            closeBtn.style.borderBottomWidth = 0;
            closeBtn.style.borderLeftWidth = 0;
            closeBtn.style.borderRightWidth = 0;
            header.Add(closeBtn);
            m_GitDiffPopup.Add(header);

            if (diffType == Git.GitLineDiffType.Modified && m_HeadFileLines != null && lineIndex < m_HeadFileLines.Count)
            {
                var originalLabel = new Label("Original (HEAD):");
                originalLabel.style.fontSize = 10;
                originalLabel.style.color = Color.gray;
                m_GitDiffPopup.Add(originalLabel);

                var codeBox = new Label(m_HeadFileLines[lineIndex]);
                codeBox.style.fontSize = 11;
                codeBox.style.color = new Color(0.9f, 0.4f, 0.4f);
                codeBox.style.backgroundColor = new Color(0.3f, 0.1f, 0.1f, 0.3f);
                codeBox.style.paddingLeft = 4;
                codeBox.style.paddingRight = 4;
                codeBox.style.paddingTop = 2;
                codeBox.style.paddingBottom = 2;
                codeBox.style.marginBottom = 6;
                m_GitDiffPopup.Add(codeBox);
            }
            else if (diffType == Git.GitLineDiffType.DeletedAbove && m_GitLineDiffResult.DeletedLinesAbove.TryGetValue(lineIndex, out var deletedList))
            {
                var delLabel = new Label($"Deleted ({deletedList.Count} lines):");
                delLabel.style.fontSize = 10;
                delLabel.style.color = Color.gray;
                m_GitDiffPopup.Add(delLabel);

                var delBox = new ScrollView();
                delBox.style.maxHeight = 100;
                foreach (var line in deletedList)
                {
                    var l = new Label("- " + line);
                    l.style.fontSize = 11;
                    l.style.color = new Color(0.9f, 0.4f, 0.4f);
                    delBox.Add(l);
                }
                delBox.style.backgroundColor = new Color(0.3f, 0.1f, 0.1f, 0.3f);
                delBox.style.paddingLeft = 4;
                delBox.style.paddingRight = 4;
                delBox.style.paddingTop = 2;
                delBox.style.paddingBottom = 2;
                delBox.style.marginBottom = 6;
                m_GitDiffPopup.Add(delBox);
            }

            // Button bar: Revert and Open in Git Window
            var btnBar = new VisualElement();
            btnBar.style.flexDirection = FlexDirection.Row;
            btnBar.style.justifyContent = Justify.FlexEnd;

            var openGitBtn = new Button(() => {
                m_GitDiffPopup.style.display = DisplayStyle.None;
                Git.GitWindow.ShowWindow();
            }) { text = "Open Git Window" };
            openGitBtn.style.fontSize = 10;
            openGitBtn.style.marginRight = 6;
            btnBar.Add(openGitBtn);

            m_GitDiffPopup.Add(btnBar);

            // Beside the clicked gutter marker (panel coordinates), below its line unless there is no room.
            PopupPlacement.Show(m_GitDiffPopup, new Rect(screenPos.x + 10, screenPos.y - LINE_HEIGHT * 0.5f, 1, LINE_HEIGHT), preferAbove: false);
        }
    }
}
