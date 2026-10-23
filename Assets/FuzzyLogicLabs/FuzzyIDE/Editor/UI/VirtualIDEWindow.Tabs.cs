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
    /// <summary>
    /// Tab conveniences: the tab context menu (close others / to the right / saved / all, copy path, reveal) and
    /// Ctrl+Tab switching between recently used files, committed when Ctrl is released.
    /// </summary>
    public partial class VirtualIDEWindow
    {
        private readonly List<string> m_TabHistory = new List<string>(); // most recent first
        private VisualElement m_TabSwitcher;
        private List<string> m_TabSwitcherItems;
        private int m_TabSwitcherIndex;
        private IVisualElementScheduledItem m_TabSwitcherTimeout;

        /// <summary>Called whenever a pane shows another file: MRU order, status bar details, file-scoped Problems.</summary>
        private void OnTabActivated(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath))
            {
                m_TabHistory.Remove(assetPath);
                m_TabHistory.Insert(0, assetPath);
            }
            RefreshStatusFileInfo();
            if (m_ProblemsCurrentFile != null && m_ProblemsCurrentFile.value) RefreshProblems();
        }

        private void AddTabContextItems(GenericMenu menu, EditorPaneState pane, string filePath)
        {
            int index = pane.OpenedTabs.IndexOf(filePath);
            menu.AddItem(new GUIContent("Close Others"), false, () => CloseTabs(pane, pane.OpenedTabs.Where(t => t != filePath)));
            if (index >= 0 && index < pane.OpenedTabs.Count - 1)
                menu.AddItem(new GUIContent("Close Tabs to the Right"), false, () => CloseTabs(pane, pane.OpenedTabs.Skip(index + 1)));
            else
                menu.AddDisabledItem(new GUIContent("Close Tabs to the Right"));
            menu.AddItem(new GUIContent("Close Saved"), false, () => CloseTabs(pane, pane.OpenedTabs.Where(t => !(m_OpenedFiles.TryGetValue(t, out var s) && s.IsDirty))));
            menu.AddItem(new GUIContent("Close All"), false, () => CloseTabs(pane, pane.OpenedTabs, includePinned: true));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Path"), false, () => EditorGUIUtility.systemCopyBuffer = Path.GetFullPath(filePath).Replace('\\', '/'));
            menu.AddItem(new GUIContent("Copy Relative Path"), false, () => EditorGUIUtility.systemCopyBuffer = ProjectDiagnosticsService.NormalizeAssetPath(filePath));
            menu.AddItem(new GUIContent("Reveal in Project Window"), false, () =>
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(ProjectDiagnosticsService.NormalizeAssetPath(filePath));
                if (asset == null) return;
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            });
            menu.AddItem(new GUIContent(Application.platform == RuntimePlatform.OSXEditor ? "Reveal in Finder" : "Reveal in File Explorer"), false,
                () => EditorUtility.RevealInFinder(Path.GetFullPath(filePath)));
        }

        // Pinned tabs survive bulk closing unless asked; CloseTab still asks about unsaved changes per file.
        private void CloseTabs(EditorPaneState pane, IEnumerable<string> tabs, bool includePinned = false)
        {
            foreach (var tab in tabs.ToList())
            {
                if (!includePinned && pane.PinnedTabs.Contains(tab)) continue;
                CloseTab(pane, tab);
            }
        }

        // ---- Ctrl+Tab switcher ----------------------------------------------------------------------------------

        private void InitializeTabSwitcher()
        {
            rootVisualElement.RegisterCallback<KeyUpEvent>(e =>
            {
                if (m_TabSwitcher != null && m_TabSwitcher.style.display == DisplayStyle.Flex &&
                    (e.keyCode == KeyCode.LeftControl || e.keyCode == KeyCode.RightControl || e.keyCode == KeyCode.LeftCommand || e.keyCode == KeyCode.RightCommand))
                {
                    CommitTabSwitcher();
                    e.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
        }

        /// <summary>Handles Ctrl+Tab / Ctrl+Shift+Tab and keys pressed while the switcher is open. True when consumed.</summary>
        private bool HandleTabSwitcherKey(KeyDownEvent evt)
        {
            bool open = m_TabSwitcher != null && m_TabSwitcher.style.display == DisplayStyle.Flex;
            if (evt.keyCode == KeyCode.Tab && (evt.ctrlKey || evt.commandKey))
            {
                if (!open) OpenTabSwitcher();
                MoveTabSwitcher(evt.shiftKey ? -1 : 1);
                return true;
            }
            if (!open) return false;
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow: MoveTabSwitcher(-1); return true;
                case KeyCode.DownArrow: MoveTabSwitcher(1); return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter: CommitTabSwitcher(); return true;
                case KeyCode.Escape: CloseTabSwitcher(); return true;
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                case KeyCode.None:
                    return true;
                default:
                    // Any other key ends the switch first (some platforms never report Ctrl being released).
                    CommitTabSwitcher();
                    return false;
            }
        }

        private void OpenTabSwitcher()
        {
            var open = new HashSet<string>((m_LeftPane?.OpenedTabs ?? new List<string>()).Concat(m_RightPane?.OpenedTabs ?? new List<string>()));
            m_TabSwitcherItems = m_TabHistory.Where(open.Contains).Concat(open.Where(t => !m_TabHistory.Contains(t))).ToList();
            if (m_TabSwitcherItems.Count == 0) return;

            if (m_TabSwitcher == null)
            {
                var theme = CodeIDESettings.Current.Theme;
                m_TabSwitcher = new VisualElement { name = "tab-switcher", pickingMode = PickingMode.Position };
                m_TabSwitcher.style.position = Position.Absolute;
                m_TabSwitcher.style.left = new Length(50, LengthUnit.Percent);
                m_TabSwitcher.style.top = new Length(18, LengthUnit.Percent);
                m_TabSwitcher.style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
                m_TabSwitcher.style.width = 460;
                m_TabSwitcher.style.maxHeight = new Length(60, LengthUnit.Percent);
                m_TabSwitcher.style.backgroundColor = theme.UIPopupBackground;
                m_TabSwitcher.style.borderTopWidth = m_TabSwitcher.style.borderBottomWidth = m_TabSwitcher.style.borderLeftWidth = m_TabSwitcher.style.borderRightWidth = 1;
                m_TabSwitcher.style.borderTopColor = m_TabSwitcher.style.borderBottomColor = m_TabSwitcher.style.borderLeftColor = m_TabSwitcher.style.borderRightColor = theme.UIBorder;
                m_TabSwitcher.style.borderTopLeftRadius = m_TabSwitcher.style.borderTopRightRadius = m_TabSwitcher.style.borderBottomLeftRadius = m_TabSwitcher.style.borderBottomRightRadius = 6;
                m_TabSwitcher.style.paddingTop = m_TabSwitcher.style.paddingBottom = 4;
                m_TabSwitcher.style.overflow = Overflow.Hidden;
                rootVisualElement.Add(m_TabSwitcher);
            }
            m_TabSwitcher.style.display = DisplayStyle.Flex;
            m_TabSwitcher.BringToFront();
            // The current file is first; the first Ctrl+Tab moves to the previous one.
            m_TabSwitcherIndex = 0;
        }

        private void MoveTabSwitcher(int delta)
        {
            if (m_TabSwitcherItems == null || m_TabSwitcherItems.Count == 0) return;
            int count = m_TabSwitcherItems.Count;
            m_TabSwitcherIndex = ((m_TabSwitcherIndex + delta) % count + count) % count;
            RenderTabSwitcher();
            m_TabSwitcherTimeout?.Pause();
            // Safety net when the Ctrl release never arrives.
            m_TabSwitcherTimeout = rootVisualElement.schedule.Execute(CommitTabSwitcher).StartingIn(2500);
        }

        private void RenderTabSwitcher()
        {
            m_TabSwitcher.Clear();
            var title = new Label("Recent Files  \u00B7  Ctrl+Tab");
            title.style.color = new Color(0.55f, 0.55f, 0.55f);
            title.style.fontSize = 10;
            title.style.paddingLeft = 10;
            title.style.paddingBottom = 2;
            m_TabSwitcher.Add(title);
            for (int i = 0; i < m_TabSwitcherItems.Count && i < 20; i++)
            {
                string path = m_TabSwitcherItems[i];
                int itemIndex = i;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.paddingLeft = 10; row.style.paddingRight = 10; row.style.paddingTop = 3; row.style.paddingBottom = 3;
                if (i == m_TabSwitcherIndex) row.style.backgroundColor = CodeIDESettings.Current.Theme.SelectionHighlight;
                bool dirty = m_OpenedFiles.TryGetValue(path, out var state) && state.IsDirty;
                var name = new Label(Path.GetFileName(path) + (dirty ? " \u25CF" : ""));
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.marginRight = 8;
                var dir = new Label(Path.GetDirectoryName(path)?.Replace('\\', '/'));
                dir.style.color = new Color(0.55f, 0.55f, 0.55f);
                dir.style.overflow = Overflow.Hidden;
                dir.style.textOverflow = TextOverflow.Ellipsis;
                dir.style.flexShrink = 1;
                row.Add(name);
                row.Add(dir);
                row.RegisterCallback<ClickEvent>(e => { m_TabSwitcherIndex = itemIndex; CommitTabSwitcher(); });
                m_TabSwitcher.Add(row);
            }
        }

        private void CommitTabSwitcher()
        {
            if (m_TabSwitcher == null || m_TabSwitcher.style.display == DisplayStyle.None) return;
            string target = m_TabSwitcherItems != null && m_TabSwitcherIndex < m_TabSwitcherItems.Count ? m_TabSwitcherItems[m_TabSwitcherIndex] : null;
            CloseTabSwitcher();
            if (target == null) return;

            // Prefer the pane that already shows the file.
            var pane = m_ActivePane != null && m_ActivePane.OpenedTabs.Contains(target) ? m_ActivePane
                : m_LeftPane != null && m_LeftPane.OpenedTabs.Contains(target) ? m_LeftPane
                : m_RightPane != null && m_RightPane.OpenedTabs.Contains(target) ? m_RightPane : null;
            if (pane == null) return;
            SetActivePane(pane);
            SwitchToTab(pane, target);
            RefreshTabBar(pane);
            pane.Viewer.Focus();
        }

        private void CloseTabSwitcher()
        {
            m_TabSwitcherTimeout?.Pause();
            if (m_TabSwitcher != null) m_TabSwitcher.style.display = DisplayStyle.None;
        }
    }
}
