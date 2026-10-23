using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public partial class VirtualIDEWindow
    {
        private string m_SelectedExplorerPath = null;
        private VisualElement m_SelectedRowVisual = null;

        // Filter states
        private bool m_FilterErrorsOnly = false;
        private bool m_FilterModifiedOnly = false;
        private Button m_FilterErrorsBtn;
        private Button m_FilterModifiedBtn;

        private void InitializeExplorerHeader(VisualElement parent)
        {
            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.justifyContent = Justify.SpaceBetween;
            headerRow.style.height = 32;
            headerRow.style.paddingLeft = 14;
            headerRow.style.paddingRight = 6;
            headerRow.style.borderBottomWidth = 1;
            headerRow.style.borderBottomColor = new Color(0.12f, 0.12f, 0.12f);

            var titleLabel = new Label("EXPLORER");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new Color(0.75f, 0.75f, 0.75f);
            titleLabel.style.fontSize = 11;
            titleLabel.style.flexGrow = 1;
            headerRow.Add(titleLabel);

            var actionsContainer = new VisualElement();
            actionsContainer.style.flexDirection = FlexDirection.Row;
            actionsContainer.style.alignItems = Align.Center;

            Button CreateHeaderButton(string iconName, string tooltip, Action onClick)
            {
                var btn = new Button(onClick) { tooltip = tooltip };
                btn.style.width = 20;
                btn.style.height = 20;
                btn.style.backgroundColor = Color.clear;
                btn.style.borderTopWidth = 0;
                btn.style.borderBottomWidth = 0;
                btn.style.borderLeftWidth = 0;
                btn.style.borderRightWidth = 0;
                btn.style.paddingTop = 0;
                btn.style.paddingBottom = 0;
                btn.style.paddingLeft = 0;
                btn.style.paddingRight = 0;
                btn.style.marginLeft = 2;
                btn.style.marginRight = 2;

                var img = new Image();
                img.image = EditorGUIUtility.IconContent(iconName).image;
                img.style.flexGrow = 1;
                btn.Add(img);

                btn.RegisterCallback<MouseEnterEvent>(_ =>
                {
                    if (btn != m_FilterErrorsBtn && btn != m_FilterModifiedBtn)
                        btn.style.backgroundColor = new Color(0.25f, 0.25f, 0.27f);
                });
                btn.RegisterCallback<MouseLeaveEvent>(_ =>
                {
                    if (btn != m_FilterErrorsBtn && btn != m_FilterModifiedBtn)
                        btn.style.backgroundColor = Color.clear;
                });
                return btn;
            }

            var newFileBtn = CreateHeaderButton("CreateAddNew", "New File / Script", () => CreateAssetDialog.OpenCreateScript(GetSelectedOrRootFolderPath()));
            var newFolderBtn = CreateHeaderButton("Folder Icon", "New Folder", () => CreateAssetDialog.OpenCreateFolder(GetSelectedOrRootFolderPath()));

            // Filter Buttons
            m_FilterErrorsBtn = CreateHeaderButton("d_console.erroricon.sml", "Filter by Errors (Show only files with errors)", () =>
            {
                m_FilterErrorsOnly = !m_FilterErrorsOnly;
                UpdateFilterButtons();
                RebuildHierarchyUI();
            });

            m_FilterModifiedBtn = CreateHeaderButton("d_Collab", "Filter by Changes (Show only modified / untracked files)", () =>
            {
                m_FilterModifiedOnly = !m_FilterModifiedOnly;
                UpdateFilterButtons();
                RebuildHierarchyUI();
            });

            var refreshBtn = CreateHeaderButton("Refresh", "Refresh Explorer", RefreshProjectScripts);
            var collapseBtn = CreateHeaderButton("d_VerticalSplit", "Collapse All Folders", CollapseAllFolders);

            actionsContainer.Add(newFileBtn);
            actionsContainer.Add(newFolderBtn);
            actionsContainer.Add(m_FilterErrorsBtn);
            actionsContainer.Add(m_FilterModifiedBtn);
            actionsContainer.Add(refreshBtn);
            actionsContainer.Add(collapseBtn);

            UpdateFilterButtons();

            headerRow.Add(actionsContainer);
            parent.Add(headerRow);
        }

        private void UpdateFilterButtons()
        {
            if (m_FilterErrorsBtn != null)
            {
                if (m_FilterErrorsOnly)
                {
                    m_FilterErrorsBtn.style.backgroundColor = new Color(0.85f, 0.2f, 0.2f, 0.45f);
                    m_FilterErrorsBtn.style.borderTopWidth = 1;
                    m_FilterErrorsBtn.style.borderBottomWidth = 1;
                    m_FilterErrorsBtn.style.borderLeftWidth = 1;
                    m_FilterErrorsBtn.style.borderRightWidth = 1;
                    m_FilterErrorsBtn.style.borderTopColor = new Color(0.95f, 0.3f, 0.3f);
                    m_FilterErrorsBtn.style.borderBottomColor = new Color(0.95f, 0.3f, 0.3f);
                    m_FilterErrorsBtn.style.borderLeftColor = new Color(0.95f, 0.3f, 0.3f);
                    m_FilterErrorsBtn.style.borderRightColor = new Color(0.95f, 0.3f, 0.3f);
                    m_FilterErrorsBtn.style.borderTopLeftRadius = 3;
                    m_FilterErrorsBtn.style.borderTopRightRadius = 3;
                    m_FilterErrorsBtn.style.borderBottomLeftRadius = 3;
                    m_FilterErrorsBtn.style.borderBottomRightRadius = 3;
                }
                else
                {
                    m_FilterErrorsBtn.style.backgroundColor = Color.clear;
                    m_FilterErrorsBtn.style.borderTopWidth = 0;
                    m_FilterErrorsBtn.style.borderBottomWidth = 0;
                    m_FilterErrorsBtn.style.borderLeftWidth = 0;
                    m_FilterErrorsBtn.style.borderRightWidth = 0;
                }
            }

            if (m_FilterModifiedBtn != null)
            {
                if (m_FilterModifiedOnly)
                {
                    m_FilterModifiedBtn.style.backgroundColor = new Color(0.15f, 0.45f, 0.85f, 0.45f);
                    m_FilterModifiedBtn.style.borderTopWidth = 1;
                    m_FilterModifiedBtn.style.borderBottomWidth = 1;
                    m_FilterModifiedBtn.style.borderLeftWidth = 1;
                    m_FilterModifiedBtn.style.borderRightWidth = 1;
                    m_FilterModifiedBtn.style.borderTopColor = new Color(0.3f, 0.6f, 1f);
                    m_FilterModifiedBtn.style.borderBottomColor = new Color(0.3f, 0.6f, 1f);
                    m_FilterModifiedBtn.style.borderLeftColor = new Color(0.3f, 0.6f, 1f);
                    m_FilterModifiedBtn.style.borderRightColor = new Color(0.3f, 0.6f, 1f);
                    m_FilterModifiedBtn.style.borderTopLeftRadius = 3;
                    m_FilterModifiedBtn.style.borderTopRightRadius = 3;
                    m_FilterModifiedBtn.style.borderBottomLeftRadius = 3;
                    m_FilterModifiedBtn.style.borderBottomRightRadius = 3;
                }
                else
                {
                    m_FilterModifiedBtn.style.backgroundColor = Color.clear;
                    m_FilterModifiedBtn.style.borderTopWidth = 0;
                    m_FilterModifiedBtn.style.borderBottomWidth = 0;
                    m_FilterModifiedBtn.style.borderLeftWidth = 0;
                    m_FilterModifiedBtn.style.borderRightWidth = 0;
                }
            }
        }

        public void RefreshExplorerHierarchy()
        {
            RefreshProjectScripts();
        }

        private void RefreshProjectScripts()
        {
            m_ProjectScripts.Clear();

            string assetsRoot = "Assets";
            if (Directory.Exists(assetsRoot))
            {
                try
                {
                    var files = Directory.GetFiles(assetsRoot, "*.*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        string normalized = file.Replace('\\', '/');
                        if (IsSupportedExtension(normalized))
                        {
                            m_ProjectScripts.Add(normalized);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FuzzyIDE] Error scanning project files: {ex.Message}");
                }
            }

            m_ProjectScripts.Sort(StringComparer.OrdinalIgnoreCase);
            RebuildHierarchyUI();
        }

        private void CollapseAllFolders()
        {
            m_ExpandedFolders.Clear();
            m_ExpandedFolders.Add("Assets");
            SaveState();
            RebuildHierarchyUI();
        }

        private string GetSelectedOrRootFolderPath()
        {
            if (!string.IsNullOrEmpty(m_SelectedExplorerPath))
            {
                if (AssetDatabase.IsValidFolder(m_SelectedExplorerPath) || Directory.Exists(m_SelectedExplorerPath))
                {
                    return m_SelectedExplorerPath;
                }
                string parent = Path.GetDirectoryName(m_SelectedExplorerPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    return parent.Replace('\\', '/');
                }
            }
            return "Assets";
        }

        private void SelectExplorerItem(string path, VisualElement rowVisual)
        {
            if (m_SelectedRowVisual != null)
            {
                m_SelectedRowVisual.style.backgroundColor = Color.clear;
            }

            m_SelectedExplorerPath = path;
            m_SelectedRowVisual = rowVisual;

            if (m_SelectedRowVisual != null)
            {
                m_SelectedRowVisual.style.backgroundColor = new Color(0.18f, 0.28f, 0.42f, 0.75f);
            }
        }

        private bool MatchesFilter(FileNode node)
        {
            if (!m_FilterErrorsOnly && !m_FilterModifiedOnly) return true;

            bool matchError = m_FilterErrorsOnly && node.ErrorCount > 0;
            bool hasMod = node.IsFolder
                ? (node.HasUnsavedChanges || GetGitStatusForFolder(node.FullPath).HasValue)
                : (node.HasUnsavedChanges || GetGitStatusForFile(node.FullPath).HasValue);
            bool matchModified = m_FilterModifiedOnly && hasMod;

            if (m_FilterErrorsOnly && m_FilterModifiedOnly)
                return matchError || matchModified;
            if (m_FilterErrorsOnly)
                return matchError;
            if (m_FilterModifiedOnly)
                return matchModified;

            return true;
        }

        private void RebuildHierarchyUI()
        {
            if (m_HierarchyScrollView == null) return;
            m_HierarchyScrollView.Clear();

            // Register background context click for empty area
            m_HierarchyScrollView.RegisterCallback<ContextClickEvent>(e =>
            {
                if (e.target == m_HierarchyScrollView || e.target == m_HierarchyScrollView.contentContainer)
                {
                    ShowEmptyAreaContextMenu();
                    e.StopPropagation();
                }
            });

            FileNode rootNode = new FileNode { Name = "Root", FullPath = "", IsFolder = true };

            // 1. Ensure directories are included
            if (Directory.Exists("Assets"))
            {
                try
                {
                    var dirs = Directory.GetDirectories("Assets", "*", SearchOption.AllDirectories);
                    foreach (var dir in dirs)
                    {
                        string normalized = dir.Replace('\\', '/');
                        InsertNodePath(rootNode, normalized, true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FuzzyIDE] Error scanning directories: {ex.Message}");
                }
            }

            // 2. Insert all supported files
            foreach (string path in m_ProjectScripts)
            {
                InsertNodePath(rootNode, path, false);
            }

            // 3. Precalculate statuses for all nodes in the tree
            PopulateNodeStatuses(rootNode);

            // 4. Render tree
            var sortedChildren = rootNode.Children.Values
                .OrderByDescending(c => c.IsFolder)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int renderedCount = 0;
            foreach (var child in sortedChildren)
            {
                if (MatchesFilter(child))
                {
                    RenderNode(child, m_HierarchyScrollView, "", 0);
                    renderedCount++;
                }
            }

            // If a filter is active and nothing matched, show friendly feedback
            if (renderedCount == 0 && (m_FilterErrorsOnly || m_FilterModifiedOnly))
            {
                var emptyBox = new VisualElement();
                emptyBox.style.paddingTop = 30;
                emptyBox.style.paddingLeft = 15;
                emptyBox.style.paddingRight = 15;
                emptyBox.style.alignItems = Align.Center;

                string msg = m_FilterErrorsOnly && m_FilterModifiedOnly
                    ? "No errors or modified files found"
                    : (m_FilterErrorsOnly ? "No errors found 🎉" : "No modified files found");

                var emptyLabel = new Label(msg);
                emptyLabel.style.color = new Color(0.6f, 0.75f, 0.6f);
                emptyLabel.style.fontSize = 12;
                emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                emptyBox.Add(emptyLabel);

                var clearBtn = new Button(() =>
                {
                    m_FilterErrorsOnly = false;
                    m_FilterModifiedOnly = false;
                    UpdateFilterButtons();
                    RebuildHierarchyUI();
                })
                {
                    text = "Clear Filter"
                };
                clearBtn.style.marginTop = 10;
                clearBtn.style.paddingLeft = 10;
                clearBtn.style.paddingRight = 10;
                clearBtn.style.fontSize = 11;
                emptyBox.Add(clearBtn);

                m_HierarchyScrollView.Add(emptyBox);
            }
        }

        private void PopulateNodeStatuses(FileNode node)
        {
            if (node.IsFolder)
            {
                var folderDiags = ProjectDiagnosticsService.GetFolderDiagnostics(node.FullPath);
                node.ErrorCount = folderDiags.errorCount;
                node.WarningCount = folderDiags.warningCount;
                node.HasUnsavedChanges = m_OpenedFiles.Values.Any(f => f.IsDirty && f.FilePath.Replace('\\', '/').StartsWith(node.FullPath + "/", StringComparison.OrdinalIgnoreCase));

                foreach (var child in node.Children.Values)
                {
                    PopulateNodeStatuses(child);
                }
            }
            else
            {
                var diags = ProjectDiagnosticsService.GetFileDiagnostics(node.FullPath);
                node.ErrorCount = diags?.ErrorCount ?? 0;
                node.WarningCount = diags?.WarningCount ?? 0;
                node.HasUnsavedChanges = m_OpenedFiles.TryGetValue(node.FullPath, out var fileState) && fileState.IsDirty;
            }
        }

        private void InsertNodePath(FileNode rootNode, string fullPath, bool isDirectory)
        {
            string[] parts = fullPath.Split('/');
            FileNode current = rootNode;
            string cumulativePath = "";

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                cumulativePath = string.IsNullOrEmpty(cumulativePath) ? part : cumulativePath + "/" + part;
                bool isFolderStep = (i < parts.Length - 1) || isDirectory;

                if (!current.Children.TryGetValue(part, out var childNode))
                {
                    childNode = new FileNode
                    {
                        Name = part,
                        FullPath = cumulativePath,
                        IsFolder = isFolderStep
                    };
                    current.Children[part] = childNode;
                }
                else
                {
                    if (isFolderStep) childNode.IsFolder = true;
                    if (string.IsNullOrEmpty(childNode.FullPath)) childNode.FullPath = cumulativePath;
                }

                current = childNode;
            }
        }

        private void RenderNode(FileNode node, VisualElement parentElement, string parentPath, int depth)
        {
            string nodePath = string.IsNullOrEmpty(parentPath) ? node.Name : parentPath + "/" + node.Name;
            node.FullPath = nodePath;

            if (node.IsFolder)
            {
                RenderFolderNode(node, parentElement, nodePath, depth);
            }
            else
            {
                RenderFileNode(node, parentElement, nodePath, depth);
            }
        }

        private void RenderFolderNode(FileNode node, VisualElement parentElement, string nodePath, int depth)
        {
            var folderContainer = new VisualElement();

            bool isFilterActive = m_FilterErrorsOnly || m_FilterModifiedOnly;
            bool isExpanded = isFilterActive || m_ExpandedFolders.Contains(nodePath) || (node.Name == "Assets" && m_ExpandedFolders.Count == 0);

            // Folder Header Row (Strictly left-aligned with custom indentation)
            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.height = 24;
            headerRow.style.paddingLeft = depth * 14 + 6;
            headerRow.style.paddingRight = 6;
            headerRow.style.cursor = new StyleCursor(StyleKeyword.None);

            // 1. Arrow indicator
            var arrow = new Label(isExpanded ? "▼" : "▶");
            arrow.style.width = 12;
            arrow.style.fontSize = 8;
            arrow.style.color = new Color(0.6f, 0.6f, 0.6f);
            arrow.style.unityTextAlign = TextAnchor.MiddleCenter;
            arrow.style.marginRight = 3;
            arrow.style.flexShrink = 0;
            headerRow.Add(arrow);

            // 2. Folder Icon
            var folderIcon = new Image();
            folderIcon.image = EditorGUIUtility.IconContent(isExpanded ? "FolderOpened Icon" : "Folder Icon").image;
            folderIcon.style.width = 16;
            folderIcon.style.height = 16;
            folderIcon.style.marginRight = 4;
            folderIcon.style.flexShrink = 0;
            headerRow.Add(folderIcon);

            // 3. Folder Name Label (Strictly left-aligned!)
            var nameLabel = new Label(node.Name);
            nameLabel.style.fontSize = 13;
            nameLabel.style.color = node.ErrorCount > 0 ? new Color(0.95f, 0.35f, 0.35f) : new Color(0.85f, 0.85f, 0.85f);
            headerRow.Add(nameLabel);

            // 4. Spacer
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            headerRow.Add(spacer);

            // 5. Badges
            var badges = new VisualElement();
            badges.style.flexDirection = FlexDirection.Row;
            badges.style.alignItems = Align.Center;

            // Dirty Dot Indicator
            if (node.HasUnsavedChanges)
            {
                var dirtyDot = new Label("●");
                dirtyDot.style.fontSize = 9;
                dirtyDot.style.color = new Color(0.85f, 0.85f, 0.85f);
                dirtyDot.style.marginRight = 4;
                badges.Add(dirtyDot);
            }

            // Git Folder Status
            var gitStatus = GetGitStatusForFolder(nodePath);
            if (gitStatus.HasValue)
            {
                var gitBadge = CreatePillBadge(gitStatus.Value.text, gitStatus.Value.color, $"Git: {gitStatus.Value.text} inside folder");
                badges.Add(gitBadge);
            }

            // Warnings Bubble-Up
            if (node.WarningCount > 0)
            {
                var warnBadge = CreatePillBadge($"⚠️{node.WarningCount}", new Color(0.95f, 0.75f, 0.25f), $"{node.WarningCount} warning(s) inside {node.Name}");
                badges.Add(warnBadge);
            }

            // Errors Bubble-Up
            if (node.ErrorCount > 0)
            {
                var errorBadge = CreatePillBadge($"🔴{node.ErrorCount}", new Color(0.95f, 0.25f, 0.25f), $"{node.ErrorCount} error(s) inside {node.Name}");
                badges.Add(errorBadge);
            }

            headerRow.Add(badges);
            folderContainer.Add(headerRow);

            // 6. Children container
            var childrenContainer = new VisualElement();
            childrenContainer.style.display = isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            folderContainer.Add(childrenContainer);

            // Selection styling
            if (nodePath == m_SelectedExplorerPath)
            {
                headerRow.style.backgroundColor = new Color(0.18f, 0.28f, 0.42f, 0.75f);
                m_SelectedRowVisual = headerRow;
            }

            Action toggleAction = () =>
            {
                isExpanded = !isExpanded;
                if (isExpanded)
                {
                    if (!m_ExpandedFolders.Contains(nodePath)) m_ExpandedFolders.Add(nodePath);
                }
                else
                {
                    m_ExpandedFolders.Remove(nodePath);
                }
                arrow.text = isExpanded ? "▼" : "▶";
                folderIcon.image = EditorGUIUtility.IconContent(isExpanded ? "FolderOpened Icon" : "Folder Icon").image;
                childrenContainer.style.display = isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
                SaveState();
            };

            arrow.RegisterCallback<MouseDownEvent>(e =>
            {
                toggleAction();
                e.StopPropagation();
            });

            headerRow.RegisterCallback<MouseDownEvent>(e =>
            {
                SelectExplorerItem(nodePath, headerRow);
                if (e.clickCount == 2)
                {
                    toggleAction();
                }
            });

            headerRow.RegisterCallback<ContextClickEvent>(e =>
            {
                SelectExplorerItem(nodePath, headerRow);
                ShowFolderContextMenu(nodePath);
                e.StopPropagation();
            });

            headerRow.RegisterCallback<MouseEnterEvent>(_ =>
            {
                if (nodePath != m_SelectedExplorerPath)
                    headerRow.style.backgroundColor = new Color(0.243f, 0.243f, 0.258f);
            });

            headerRow.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                if (nodePath != m_SelectedExplorerPath)
                    headerRow.style.backgroundColor = Color.clear;
            });

            parentElement.Add(folderContainer);

            // Render children
            var sortedChildren = node.Children.Values
                .OrderByDescending(c => c.IsFolder)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var child in sortedChildren)
            {
                if (MatchesFilter(child))
                {
                    RenderNode(child, childrenContainer, nodePath, depth + 1);
                }
            }
        }

        private void RenderFileNode(FileNode node, VisualElement parentElement, string nodePath, int depth)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 24;
            // Indent depth * 14 + 6 + 15 (15px aligns file icon exactly with folder icon above!)
            row.style.paddingLeft = depth * 14 + 6 + 15;
            row.style.paddingRight = 6;
            row.style.cursor = new StyleCursor(StyleKeyword.None);

            // Type Icon
            var icon = new Image();
            icon.style.width = 16;
            icon.style.height = 16;
            icon.style.flexShrink = 0;
            icon.style.marginRight = 4;
            string ext = Path.GetExtension(node.FullPath).ToLower();

            if (ext == ".cs") icon.image = EditorGUIUtility.IconContent("cs Script Icon").image;
            else if (ext == ".json") icon.image = EditorGUIUtility.IconContent("TextAsset Icon").image;
            else if (ext == ".shader" || ext == ".hlsl" || ext == ".cginc") icon.image = EditorGUIUtility.IconContent("Shader Icon").image;
            else if (ext == ".asmdef") icon.image = EditorGUIUtility.IconContent("AssemblyDefinitionAsset Icon").image;
            else icon.image = EditorGUIUtility.IconContent("TextAsset Icon").image;

            row.Add(icon);

            // Dirty indicator dot
            if (node.HasUnsavedChanges)
            {
                var dirtyDot = new Label("●");
                dirtyDot.style.fontSize = 9;
                dirtyDot.style.color = new Color(0.9f, 0.9f, 0.9f);
                dirtyDot.style.marginRight = 4;
                dirtyDot.tooltip = "Unsaved changes in FuzzyIDE";
                row.Add(dirtyDot);
            }

            // File Name Label
            var label = new Label(node.Name);
            label.style.fontSize = 13;

            // Determine text color based on errors > git > normal
            if (node.ErrorCount > 0)
            {
                label.style.color = new Color(0.95f, 0.35f, 0.35f);
            }
            else if (node.WarningCount > 0)
            {
                label.style.color = new Color(0.95f, 0.8f, 0.35f);
            }
            else
            {
                var gitStatus = GetGitStatusForFile(node.FullPath);
                if (gitStatus.HasValue)
                {
                    label.style.color = gitStatus.Value.color;
                }
                else
                {
                    label.style.color = new Color(0.85f, 0.85f, 0.85f);
                }
            }
            row.Add(label);

            // Spacer
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            // Badges container
            var badges = new VisualElement();
            badges.style.flexDirection = FlexDirection.Row;
            badges.style.alignItems = Align.Center;

            // Git Badge
            var git = GetGitStatusForFile(node.FullPath);
            if (git.HasValue)
            {
                var gitBadge = CreatePillBadge(git.Value.text, git.Value.color, $"Git status: {git.Value.text}");
                badges.Add(gitBadge);
            }

            // Warnings Badge
            if (node.WarningCount > 0)
            {
                var warnBadge = CreatePillBadge($"⚠️{node.WarningCount}", new Color(0.95f, 0.75f, 0.25f), $"{node.WarningCount} warning(s)");
                badges.Add(warnBadge);
            }

            // Errors Badge
            if (node.ErrorCount > 0)
            {
                var diags = ProjectDiagnosticsService.GetFileDiagnostics(nodePath);
                string errorTooltip = $"{node.ErrorCount} error(s)";
                if (diags != null && diags.Items.Count > 0)
                {
                    var firstErrors = diags.Items.Where(i => i.IsError).Take(3).Select(i => $"Line {i.Line}: {i.Message}");
                    errorTooltip += "\n\n" + string.Join("\n", firstErrors);
                }

                var errBadge = CreatePillBadge($"🔴{node.ErrorCount}", new Color(0.95f, 0.25f, 0.25f), errorTooltip);
                errBadge.RegisterCallback<MouseDownEvent>(e =>
                {
                    if (diags != null && diags.Items.Count > 0)
                    {
                        var firstErr = diags.Items.FirstOrDefault(i => i.IsError);
                        LoadFileAndJumpToLine(node.FullPath, firstErr.Line);
                        e.StopPropagation();
                    }
                });
                badges.Add(errBadge);
            }

            row.Add(badges);

            // Selection Check
            if (nodePath == m_SelectedExplorerPath)
            {
                row.style.backgroundColor = new Color(0.18f, 0.28f, 0.42f, 0.75f);
                m_SelectedRowVisual = row;
            }

            // Mouse Events
            row.RegisterCallback<MouseDownEvent>(e =>
            {
                SelectExplorerItem(nodePath, row);

                if (e.clickCount == 2)
                {
                    LoadFileAndJumpToLine(node.FullPath, -1);
                }
            });

            row.RegisterCallback<ContextClickEvent>(e =>
            {
                SelectExplorerItem(nodePath, row);
                ShowFileContextMenu(node.FullPath);
                e.StopPropagation();
            });

            row.RegisterCallback<MouseEnterEvent>(_ =>
            {
                if (nodePath != m_SelectedExplorerPath)
                {
                    row.style.backgroundColor = new Color(0.243f, 0.243f, 0.258f);
                }
            });

            row.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                if (nodePath != m_SelectedExplorerPath)
                {
                    row.style.backgroundColor = Color.clear;
                }
            });

            parentElement.Add(row);
        }

        private VisualElement CreatePillBadge(string text, Color color, string tooltip)
        {
            var badge = new Label(text)
            {
                tooltip = tooltip
            };
            badge.style.fontSize = 10;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = color;
            badge.style.marginLeft = 4;
            badge.style.paddingLeft = 3;
            badge.style.paddingRight = 3;
            badge.style.paddingTop = 1;
            badge.style.paddingBottom = 1;
            badge.style.borderTopLeftRadius = 3;
            badge.style.borderTopRightRadius = 3;
            badge.style.borderBottomLeftRadius = 3;
            badge.style.borderBottomRightRadius = 3;
            badge.style.backgroundColor = new Color(color.r, color.g, color.b, 0.15f);
            return badge;
        }

        private void ShowFolderContextMenu(string folderPath)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("New C# Script..."), false, () => CreateAssetDialog.OpenCreateScript(folderPath));
            menu.AddItem(new GUIContent("New Folder..."), false, () => CreateAssetDialog.OpenCreateFolder(folderPath));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("New File..."), false, () => CreateAssetDialog.OpenCreateFile(folderPath));
            menu.AddItem(new GUIContent("New Shader..."), false, () =>
            {
                ProjectAssetOperations.CreateGenericFile(folderPath, "NewShader", ".shader", ProjectAssetOperations.GenerateShaderTemplate("NewShader"), out string p, out _);
                if (!string.IsNullOrEmpty(p)) LoadFileAndJumpToLine(p, 0);
            });
            menu.AddItem(new GUIContent("New Asmdef..."), false, () =>
            {
                ProjectAssetOperations.CreateGenericFile(folderPath, "NewAssembly", ".asmdef", ProjectAssetOperations.GenerateAsmdefTemplate("NewAssembly"), out string p, out _);
                if (!string.IsNullOrEmpty(p)) LoadFileAndJumpToLine(p, 0);
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Rename... (F2)"), false, () => CreateAssetDialog.OpenRename(folderPath));
            menu.AddItem(new GUIContent("Delete (Delete)"), false, () => ProjectAssetOperations.DeleteAsset(folderPath));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Reveal in Explorer"), false, () => ProjectAssetOperations.RevealInExplorer(folderPath));
            menu.AddItem(new GUIContent("Ping in Project Window"), false, () => ProjectAssetOperations.PingInProjectWindow(folderPath));
            menu.AddItem(new GUIContent("Copy Path"), false, () => EditorGUIUtility.systemCopyBuffer = folderPath);
            menu.AddItem(new GUIContent("Copy Relative Path"), false, () => EditorGUIUtility.systemCopyBuffer = ProjectDiagnosticsService.NormalizeAssetPath(folderPath));

            menu.ShowAsContext();
        }

        private void ShowFileContextMenu(string filePath)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Open"), false, () => LoadFileAndJumpToLine(filePath, -1));
            menu.AddItem(new GUIContent("Open to Side (Split View)"), false, () =>
            {
                if (!m_IsRightPaneOpen) ToggleSplitView();
                var targetPane = (m_ActivePane == m_LeftPane) ? m_RightPane : m_LeftPane;
                if (!targetPane.OpenedTabs.Contains(filePath)) targetPane.OpenedTabs.Add(filePath);
                SetActivePane(targetPane);
                SwitchToTab(targetPane, filePath);
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Duplicate (Ctrl+D)"), false, () =>
            {
                if (ProjectAssetOperations.DuplicateAsset(filePath, out string newPath, out _))
                {
                    LoadFileAndJumpToLine(newPath, -1);
                }
            });
            menu.AddItem(new GUIContent("Rename... (F2)"), false, () => CreateAssetDialog.OpenRename(filePath));
            menu.AddItem(new GUIContent("Delete (Delete)"), false, () =>
            {
                if (ProjectAssetOperations.DeleteAsset(filePath))
                {
                    OnAssetDeletedInProject(filePath);
                }
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Reveal in Explorer"), false, () => ProjectAssetOperations.RevealInExplorer(filePath));
            menu.AddItem(new GUIContent("Ping in Project Window"), false, () => ProjectAssetOperations.PingInProjectWindow(filePath));
            menu.AddItem(new GUIContent("Copy Path"), false, () => EditorGUIUtility.systemCopyBuffer = filePath);
            menu.AddItem(new GUIContent("Copy Relative Path"), false, () => EditorGUIUtility.systemCopyBuffer = ProjectDiagnosticsService.NormalizeAssetPath(filePath));

            menu.ShowAsContext();
        }

        private void ShowEmptyAreaContextMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("New C# Script..."), false, () => CreateAssetDialog.OpenCreateScript("Assets"));
            menu.AddItem(new GUIContent("New Folder..."), false, () => CreateAssetDialog.OpenCreateFolder("Assets"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Refresh Explorer"), false, RefreshProjectScripts);
            menu.AddItem(new GUIContent("Collapse All Folders"), false, CollapseAllFolders);
            menu.ShowAsContext();
        }

        public void OnProjectAssetsChanged(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (deleted != null)
            {
                foreach (var d in deleted)
                {
                    OnAssetDeletedInProject(d);
                }
            }

            if (moved != null && movedFrom != null)
            {
                for (int i = 0; i < moved.Length && i < movedFrom.Length; i++)
                {
                    OnAssetRenamedInProject(movedFrom[i], moved[i]);
                }
            }

            RefreshProjectScripts();
        }

        public void OnAssetRenamedInProject(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;

            string normOld = oldPath.Replace('\\', '/');
            string normNew = newPath.Replace('\\', '/');

            if (m_OpenedFiles.TryGetValue(normOld, out var state))
            {
                m_OpenedFiles.Remove(normOld);
                state.FilePath = normNew;
                state.FileName = Path.GetFileName(normNew);
                m_OpenedFiles[normNew] = state;
            }

            if (m_LeftPane != null)
            {
                int idx = m_LeftPane.OpenedTabs.IndexOf(normOld);
                if (idx >= 0) m_LeftPane.OpenedTabs[idx] = normNew;
                if (m_LeftPane.ActiveTab == normOld) m_LeftPane.ActiveTab = normNew;
                RefreshTabBar(m_LeftPane);
            }

            if (m_RightPane != null)
            {
                int idx = m_RightPane.OpenedTabs.IndexOf(normOld);
                if (idx >= 0) m_RightPane.OpenedTabs[idx] = normNew;
                if (m_RightPane.ActiveTab == normOld) m_RightPane.ActiveTab = normNew;
                RefreshTabBar(m_RightPane);
            }

            if (m_SelectedExplorerPath == normOld)
            {
                m_SelectedExplorerPath = normNew;
            }

            UpdateWindowTitle();
            RefreshProjectScripts();
        }

        public void OnAssetDeletedInProject(string deletedPath)
        {
            if (string.IsNullOrEmpty(deletedPath)) return;
            string norm = deletedPath.Replace('\\', '/');

            var toRemove = m_OpenedFiles.Keys.Where(k => k.Equals(norm, StringComparison.OrdinalIgnoreCase) || k.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var p in toRemove)
            {
                if (m_LeftPane != null && m_LeftPane.OpenedTabs.Contains(p))
                {
                    m_LeftPane.OpenedTabs.Remove(p);
                    if (m_LeftPane.ActiveTab == p) SwitchToTab(m_LeftPane, m_LeftPane.OpenedTabs.FirstOrDefault());
                    RefreshTabBar(m_LeftPane);
                }
                if (m_RightPane != null && m_RightPane.OpenedTabs.Contains(p))
                {
                    m_RightPane.OpenedTabs.Remove(p);
                    if (m_RightPane.ActiveTab == p) SwitchToTab(m_RightPane, m_RightPane.OpenedTabs.FirstOrDefault());
                    RefreshTabBar(m_RightPane);
                }
                m_OpenedFiles.Remove(p);
                ProjectDiagnosticsService.ClearRoslynDiagnostics(p);
            }

            if (m_SelectedExplorerPath == norm)
            {
                m_SelectedExplorerPath = null;
                m_SelectedRowVisual = null;
            }

            UpdateWindowTitle();
            RefreshProjectScripts();
        }
    }
}
