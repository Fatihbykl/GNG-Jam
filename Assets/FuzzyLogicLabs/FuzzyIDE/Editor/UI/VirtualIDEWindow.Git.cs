using System;
using System.IO;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Git;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualIDEWindow
    {
        private Button m_GitToolbarBtn;
        private Label m_GitToolbarBranchLabel;
        private Label m_GitToolbarCountLabel;

        private void InitializeGitIntegration()
        {
            GitService.OnStatusUpdated -= OnGitStatusUpdatedForIDE;
            GitService.OnStatusUpdated += OnGitStatusUpdatedForIDE;

            // Trigger initial Git check
            if (GitService.IsGitRepository)
            {
                UpdateGitToolbarState(GitService.CurrentStatus);
            }
        }

        private void InitializeGitToolbar(VisualElement toolbar)
        {
            m_GitToolbarBtn = new Button(ToggleGitWindow)
            {
                tooltip = "Open Git Source Control"
            };
            m_GitToolbarBtn.style.flexDirection = FlexDirection.Row;
            m_GitToolbarBtn.style.alignItems = Align.Center;
            m_GitToolbarBtn.style.height = 24;
            m_GitToolbarBtn.style.backgroundColor = new Color(0.20f, 0.20f, 0.23f, 0.6f);
            m_GitToolbarBtn.style.borderTopWidth = 1;
            m_GitToolbarBtn.style.borderBottomWidth = 1;
            m_GitToolbarBtn.style.borderLeftWidth = 1;
            m_GitToolbarBtn.style.borderRightWidth = 1;
            m_GitToolbarBtn.style.borderTopColor = new Color(0.30f, 0.30f, 0.34f, 0.5f);
            m_GitToolbarBtn.style.borderBottomColor = new Color(0.30f, 0.30f, 0.34f, 0.5f);
            m_GitToolbarBtn.style.borderLeftColor = new Color(0.30f, 0.30f, 0.34f, 0.5f);
            m_GitToolbarBtn.style.borderRightColor = new Color(0.30f, 0.30f, 0.34f, 0.5f);
            m_GitToolbarBtn.style.borderTopLeftRadius = 12;
            m_GitToolbarBtn.style.borderBottomLeftRadius = 12;
            m_GitToolbarBtn.style.borderTopRightRadius = 12;
            m_GitToolbarBtn.style.borderBottomRightRadius = 12;
            m_GitToolbarBtn.style.paddingLeft = 8;
            m_GitToolbarBtn.style.paddingRight = 8;
            m_GitToolbarBtn.style.marginTop = 2;
            m_GitToolbarBtn.style.marginBottom = 2;
            m_GitToolbarBtn.style.marginLeft = 2;
            m_GitToolbarBtn.style.marginRight = 2;

            var gitIcon = new Image();
            gitIcon.image = EditorGUIUtility.IconContent("d_Collab").image;
            gitIcon.style.width = 14;
            gitIcon.style.height = 14;
            gitIcon.style.marginRight = 4;
            m_GitToolbarBtn.Add(gitIcon);

            m_GitToolbarBranchLabel = new Label("Git");
            m_GitToolbarBranchLabel.style.fontSize = 11;
            m_GitToolbarBranchLabel.style.color = new Color(0.85f, 0.85f, 0.85f);
            m_GitToolbarBtn.Add(m_GitToolbarBranchLabel);

            m_GitToolbarCountLabel = new Label("");
            m_GitToolbarCountLabel.style.fontSize = 10;
            m_GitToolbarCountLabel.style.color = new Color(0.95f, 0.75f, 0.35f);
            m_GitToolbarCountLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_GitToolbarCountLabel.style.marginLeft = 3;
            m_GitToolbarBtn.Add(m_GitToolbarCountLabel);

            m_GitToolbarBtn.RegisterCallback<MouseEnterEvent>(e =>
            {
                m_GitToolbarBtn.style.backgroundColor = new Color(0.26f, 0.26f, 0.30f, 0.85f);
                m_GitToolbarBtn.style.borderTopColor = new Color(0.40f, 0.40f, 0.46f, 0.7f);
                m_GitToolbarBtn.style.borderBottomColor = new Color(0.40f, 0.40f, 0.46f, 0.7f);
                m_GitToolbarBtn.style.borderLeftColor = new Color(0.40f, 0.40f, 0.46f, 0.7f);
                m_GitToolbarBtn.style.borderRightColor = new Color(0.40f, 0.40f, 0.46f, 0.7f);
            });
            m_GitToolbarBtn.RegisterCallback<MouseLeaveEvent>(e =>
            {
                m_GitToolbarBtn.style.backgroundColor = new Color(0.20f, 0.20f, 0.23f, 0.6f);
                m_GitToolbarBtn.style.borderTopColor = new Color(0.30f, 0.30f, 0.34f, 0.5f);
                m_GitToolbarBtn.style.borderBottomColor = new Color(0.30f, 0.30f, 0.34f, 0.5f);
                m_GitToolbarBtn.style.borderLeftColor = new Color(0.30f, 0.30f, 0.34f, 0.5f);
                m_GitToolbarBtn.style.borderRightColor = new Color(0.30f, 0.30f, 0.34f, 0.5f);
            });

            toolbar.Add(m_GitToolbarBtn);

            UpdateGitToolbarState(GitService.CurrentStatus);
        }

        private void ToggleGitWindow()
        {
            GitWindow.ShowWindow();
        }

        private void OnGitStatusUpdatedForIDE(GitRepositoryStatus status)
        {
            UpdateGitToolbarState(status);

            if (CodeIDESettings.Current.Git.ShowFileTreeBadges)
            {
                RebuildHierarchyUI();
            }
        }

        private void UpdateGitToolbarState(GitRepositoryStatus status)
        {
            if (m_GitToolbarBranchLabel == null) return;

            if (status.IsGitRepository && !string.IsNullOrEmpty(status.CurrentBranch))
            {
                string operation = status.OperationState switch
                {
                    GitRepoState.Merging => "merging",
                    GitRepoState.Rebasing => "rebasing",
                    GitRepoState.CherryPicking => "cherry-picking",
                    GitRepoState.Reverting => "reverting",
                    _ => null
                };
                m_GitToolbarBranchLabel.text = operation == null ? status.CurrentBranch : $"{status.CurrentBranch} ({operation})";
                m_GitToolbarBranchLabel.style.color = operation == null || status.ConflictedFiles.Count == 0
                    ? new Color(0.85f, 0.85f, 0.85f)
                    : new Color(1f, 0.45f, 0.45f);
                int total = status.TotalChangesCount;
                if (total > 0)
                {
                    m_GitToolbarCountLabel.text = $"*{total}";
                    m_GitToolbarCountLabel.style.display = DisplayStyle.Flex;
                }
                else
                {
                    m_GitToolbarCountLabel.style.display = DisplayStyle.None;
                }
                m_GitToolbarBtn.tooltip = status.ConflictedFiles.Count > 0
                    ? $"Git: {status.CurrentBranch} — {status.ConflictedFiles.Count} conflicted file(s), {total} changes"
                    : $"Git: {status.CurrentBranch} ({total} changes)";
            }
            else
            {
                m_GitToolbarBranchLabel.text = "Git";
                m_GitToolbarCountLabel.style.display = DisplayStyle.None;
                m_GitToolbarBtn.tooltip = "No Git repository detected (Click to initialize)";
            }
        }

        private (string text, Color color)? GetGitStatusForFile(string fullPath)
        {
            if (!CodeIDESettings.Current.Git.ShowFileTreeBadges || !GitService.IsGitRepository || string.IsNullOrEmpty(fullPath))
                return null;

            string repoRoot = GitService.RepositoryRoot;
            if (string.IsNullOrEmpty(repoRoot)) return null;

            string normalizedFullPath = fullPath.Replace('\\', '/');
            string normalizedRoot = repoRoot.Replace('\\', '/');

            if (!normalizedFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                normalizedFullPath = Path.Combine(Directory.GetCurrentDirectory(), fullPath).Replace('\\', '/');
                if (!normalizedFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            string relPath = normalizedFullPath.Substring(normalizedRoot.Length).TrimStart('/');

            var status = GitService.CurrentStatus;
            GitFileStatus matched = status.ConflictedFiles.FirstOrDefault(f => string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase))
                                 ?? status.UnstagedFiles.FirstOrDefault(f => string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase))
                                 ?? status.StagedFiles.FirstOrDefault(f => string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase))
                                 ?? status.UntrackedFiles.FirstOrDefault(f => string.Equals(f.Path, relPath, StringComparison.OrdinalIgnoreCase));

            if (matched != null)
            {
                bool isStaged = status.StagedFiles.Contains(matched);
                return (matched.GetBadgeText(isStaged), matched.GetBadgeColor(isStaged));
            }

            return null;
        }

        private (string text, Color color)? GetGitStatusForFolder(string folderFullPath)
        {
            if (!CodeIDESettings.Current.Git.ShowFileTreeBadges || !GitService.IsGitRepository || string.IsNullOrEmpty(folderFullPath))
                return null;

            string repoRoot = GitService.RepositoryRoot;
            if (string.IsNullOrEmpty(repoRoot)) return null;

            string normalizedFullPath = folderFullPath.Replace('\\', '/');
            string normalizedRoot = repoRoot.Replace('\\', '/');

            if (!normalizedFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                normalizedFullPath = Path.Combine(Directory.GetCurrentDirectory(), folderFullPath).Replace('\\', '/');
                if (!normalizedFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            string relPath = normalizedFullPath.Substring(normalizedRoot.Length).TrimStart('/');
            if (!string.IsNullOrEmpty(relPath) && !relPath.EndsWith("/"))
            {
                relPath += "/";
            }

            var status = GitService.CurrentStatus;
            if (status.ConflictedFiles.Any(f => f.Path.StartsWith(relPath, StringComparison.OrdinalIgnoreCase)))
            {
                return ("C", new Color(1f, 0.3f, 0.3f, 1f));
            }

            bool hasModified = status.UnstagedFiles.Any(f => f.Path.StartsWith(relPath, StringComparison.OrdinalIgnoreCase)) ||
                               status.StagedFiles.Any(f => f.Path.StartsWith(relPath, StringComparison.OrdinalIgnoreCase));
            if (hasModified)
            {
                return ("M", CodeIDESettings.Current.Theme.GitModified);
            }

            bool hasUntracked = status.UntrackedFiles.Any(f => f.Path.StartsWith(relPath, StringComparison.OrdinalIgnoreCase));
            if (hasUntracked)
            {
                return ("U", CodeIDESettings.Current.Theme.GitAdded);
            }

            return null;
        }

        private void ApplyGitStatusToNode(string fullPath, Label label, VisualElement row)
        {
            var res = GetGitStatusForFile(fullPath);
            if (res.HasValue)
            {
                label.style.color = res.Value.color;

                var badge = new Label(res.Value.text);
                badge.style.fontSize = 10;
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                badge.style.color = res.Value.color;
                badge.style.marginLeft = 6;
                badge.style.unityTextAlign = TextAnchor.MiddleCenter;
                row.Add(badge);
            }
        }
    }
}
