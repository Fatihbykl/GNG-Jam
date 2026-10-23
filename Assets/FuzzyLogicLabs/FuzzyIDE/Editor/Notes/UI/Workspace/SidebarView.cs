using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// A sidebar with an icon tab strip on top (Files, Search, Bookmarks… / Outline, Backlinks…), like Obsidian's
    /// side docks. Only the selected pane is shown; panes are told when they become visible so expensive panes
    /// (unlinked mentions, graphs) only work while someone can see them.
    /// </summary>
    public class SidebarView : VisualElement
    {
        private sealed class Pane
        {
            public string Id;
            public Button Tab;
            public VisualElement Content;
            public Action<bool> VisibilityChanged;
            public bool Visible;
        }

        private readonly string m_PrefKey;
        private readonly VisualElement m_TabStrip;
        private readonly VisualElement m_Host;
        private readonly List<Pane> m_Panes = new List<Pane>();
        private Pane m_Active;
        private bool m_Collapsed;

        public string ActivePaneId => m_Active?.Id;
        public bool IsCollapsed => m_Collapsed;

        public SidebarView(string prefKey)
        {
            m_PrefKey = prefKey;
            style.flexGrow = 1;
            style.minWidth = 0;
            style.backgroundColor = NotesTheme.BgSecondary;

            m_TabStrip = new VisualElement
            {
                style = { height = 36, paddingLeft = 6, paddingRight = 6, borderBottomColor = NotesTheme.BorderSubtle, justifyContent = Justify.Center }
            }.WithClass("fn-toolbar");
            Add(m_TabStrip);

            m_Host = new VisualElement { style = { flexGrow = 1, minHeight = 0 } }.WithClass("fn-sidebar-host");
            Add(m_Host);
        }

        public void AddPane(string id, string icon, string title, VisualElement content, Action<bool> visibilityChanged = null)
        {
            var pane = new Pane { Id = id, Content = content, VisibilityChanged = visibilityChanged };
            pane.Tab = NotesIcons.IconButton(icon, title, () => Show(id));
            m_TabStrip.Add(pane.Tab);

            content.style.flexGrow = 1;
            content.style.display = DisplayStyle.None;
            m_Host.Add(content);
            m_Panes.Add(pane);

            if (m_Active == null || EditorPrefs.GetString(m_PrefKey, "") == id) Show(id);
        }

        public void Show(string id)
        {
            var pane = m_Panes.Find(p => p.Id == id);
            if (pane == null) return;
            m_Active = pane;
            EditorPrefs.SetString(m_PrefKey, id);
            foreach (var p in m_Panes)
            {
                p.Content.style.display = p == pane ? DisplayStyle.Flex : DisplayStyle.None;
                NotesIcons.SetToggled(p.Tab, p == pane);
            }
            UpdateVisibility();
        }

        /// <summary>Called by the window when the sidebar is collapsed or expanded.</summary>
        public void SetCollapsed(bool collapsed)
        {
            if (m_Collapsed && !collapsed)
            {
                // fade the content in (the USS transition animates the opacity)
                m_Host.style.opacity = 0f;
                schedule.Execute(() => m_Host.style.opacity = 1f);
            }
            m_Collapsed = collapsed;
            UpdateVisibility();
        }

        public bool IsPaneVisible(string id) => !m_Collapsed && m_Active != null && m_Active.Id == id;

        private void UpdateVisibility()
        {
            foreach (var p in m_Panes)
            {
                bool visible = !m_Collapsed && p == m_Active;
                if (visible == p.Visible) continue;
                p.Visible = visible;
                p.VisibilityChanged?.Invoke(visible);
            }
        }
    }
}
