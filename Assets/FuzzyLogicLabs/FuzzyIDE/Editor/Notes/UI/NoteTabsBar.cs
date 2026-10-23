using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>Drag payload of a tab being dragged (to reorder it, move it to another pane or split).</summary>
    public sealed class TabDragData
    {
        public WorkspaceLeaf Leaf;
        public int Index;
    }

    /// <summary>
    /// Tab strip of one pane. Middle-click or × closes a tab, dragging reorders or moves it, right-click opens the
    /// tab menu, + opens the quick switcher for this pane. Notes dragged from the file list can be dropped here too.
    /// </summary>
    public class NoteTabsBar : VisualElement
    {
        public const string DragTabKey = "FuzzyNotes.DragTab";

        public struct TabInfo
        {
            public string Title;
            public string Icon;
            public string Tooltip;
            public bool Dirty;
        }

        private readonly WorkspaceLeaf m_Leaf;
        private readonly VisualElement m_Strip;
        private readonly VisualElement m_InsertMarker;
        private readonly List<VisualElement> m_TabElements = new List<VisualElement>();
        private Label m_ActiveLabel;
        private NotesIcon m_ActiveIcon;

        public Func<WorkspaceTab, TabInfo> Describe;

        public event Action<int> OnTabSelected;
        public event Action<int> OnTabClosed;
        public event Action<int> OnTabContextClick;
        public event Action OnNewTabClicked;
        /// <summary>A tab (<see cref="TabDragData"/>) or a note (<see cref="NoteItem"/>) was dropped at an insert index.</summary>
        public event Action<object, int> OnDropped;

        public NoteTabsBar(WorkspaceLeaf leaf)
        {
            m_Leaf = leaf;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.FlexEnd;
            style.height = 33;
            style.flexShrink = 0;
            style.paddingLeft = 6;
            style.backgroundColor = NotesTheme.BgSecondary;
            style.borderBottomWidth = 1;
            style.borderBottomColor = NotesTheme.BorderSubtle;

            m_Strip = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexEnd, flexShrink = 1, overflow = Overflow.Hidden, height = Length.Percent(100) } };
            Add(m_Strip);

            var plus = NotesIcons.IconButton("plus", "Open a note in this pane", () => OnNewTabClicked?.Invoke(), 15f);
            plus.style.alignSelf = Align.Center;
            plus.style.marginLeft = 2;
            Add(plus);

            // empty strip space: double-click also opens the switcher (like a browser's new tab)
            var filler = new VisualElement { style = { flexGrow = 1, height = Length.Percent(100) } };
            filler.RegisterCallback<ClickEvent>(e => { if (e.clickCount == 2) OnNewTabClicked?.Invoke(); });
            Add(filler);

            m_InsertMarker = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style = { position = Position.Absolute, top = 6, bottom = 2, width = 2, backgroundColor = NotesTheme.Accent, display = DisplayStyle.None }
            };
            Add(m_InsertMarker);

            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);
            RegisterCallback<DragLeaveEvent>(_ => m_InsertMarker.style.display = DisplayStyle.None);
            RegisterCallback<DragExitedEvent>(_ => m_InsertMarker.style.display = DisplayStyle.None);
        }

        public void SetTabs(IReadOnlyList<WorkspaceTab> tabs, int activeIndex, bool paneFocused)
        {
            m_Strip.Clear();
            m_TabElements.Clear();
            m_ActiveLabel = null;
            m_ActiveIcon = null;

            for (int i = 0; i < tabs.Count; i++)
            {
                int index = i;
                bool isActive = i == activeIndex;
                var info = Describe != null ? Describe(tabs[i]) : new TabInfo { Title = tabs[i].Key };

                var tab = new VisualElement { tooltip = info.Tooltip }.WithClass("fn-tab");
                if (isActive)
                {
                    tab.AddToClassList("fn-tab--active");
                    tab.style.backgroundColor = NotesTheme.BgPrimary;
                }

                Color textColor = isActive && paneFocused ? NotesTheme.TextNormal : NotesTheme.TextMuted;
                NotesIcon icon = null;
                if (!string.IsNullOrEmpty(info.Icon))
                {
                    icon = NotesIcons.Create(info.Icon, 14f, textColor);
                    icon.style.marginRight = 6;
                    tab.Add(icon);
                }
                var label = new Label(string.IsNullOrWhiteSpace(info.Title) ? "Untitled" : info.Title) { style = { color = textColor } }.WithClass("fn-tab-label");
                tab.Add(label);
                if (isActive)
                {
                    m_ActiveLabel = label;
                    m_ActiveIcon = icon;
                }

                if (info.Dirty)
                    tab.Add(new VisualElement
                    {
                        tooltip = "Unsaved changes",
                        style =
                        {
                            width = 6, height = 6, marginLeft = 6, marginRight = 2, flexShrink = 0,
                            borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                            backgroundColor = NotesTheme.TextMuted
                        }
                    });

                var close = NotesIcons.IconButton("x", "Close tab", () => OnTabClosed?.Invoke(index), 14f);
                close.AddToClassList("fn-icon-btn--small");
                close.style.marginLeft = 2;
                close.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                tab.Add(close);

                tab.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button == 2) { OnTabClosed?.Invoke(index); e.StopPropagation(); }
                });
                // Select on click, not pointer down: selecting rebuilds the strip, which would cancel a drag
                // that starts with the same press.
                tab.RegisterCallback<ClickEvent>(e => { if (e.button == 0 && !isActive) OnTabSelected?.Invoke(index); });
                tab.RegisterCallback<ContextClickEvent>(e =>
                {
                    OnTabContextClick?.Invoke(index);
                    e.StopPropagation();
                });
                MakeDragSource(tab, index, info.Title);

                m_Strip.Add(tab);
                m_TabElements.Add(tab);
            }
        }

        /// <summary>
        /// Shows whether this pane has focus (active tab drawn at full strength) without rebuilding the strip,
        /// so a press that focuses the pane can still become a click or a drag on the same tab.
        /// </summary>
        public void SetFocused(bool focused)
        {
            var color = focused ? NotesTheme.TextNormal : NotesTheme.TextMuted;
            if (m_ActiveLabel != null) m_ActiveLabel.style.color = color;
            m_ActiveIcon?.SetColor(color);
        }

        // =====================================================================
        // Drag & drop
        // =====================================================================

        private void MakeDragSource(VisualElement tab, int index, string title)
        {
            Vector2 start = Vector2.zero;
            bool armed = false;
            tab.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) { armed = true; start = e.position; } });
            tab.RegisterCallback<PointerUpEvent>(_ => armed = false);
            tab.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!armed || (e.pressedButtons & 1) == 0) { armed = false; return; }
                if (((Vector2)e.position - start).sqrMagnitude < 36f) return;
                armed = false;
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new UnityEngine.Object[0];
                DragAndDrop.paths = new string[0];
                DragAndDrop.SetGenericData(DragTabKey, new TabDragData { Leaf = m_Leaf, Index = index });
                DragAndDrop.StartDrag(title ?? "Tab");
            });
        }

        /// <summary>The tab or note being dragged, if any.</summary>
        public static object CurrentPayload()
        {
            if (DragAndDrop.GetGenericData(DragTabKey) is TabDragData tab) return tab;
            if (DragAndDrop.GetGenericData(FileExplorerView.DragNoteKey) is NoteItem note) return note;
            return null;
        }

        public static void ClearPayload()
        {
            DragAndDrop.SetGenericData(DragTabKey, null);
            DragAndDrop.SetGenericData(FileExplorerView.DragNoteKey, null);
        }

        private int InsertIndexAt(Vector2 worldPos, out float markerX)
        {
            markerX = 6f;
            for (int i = 0; i < m_TabElements.Count; i++)
            {
                var rect = m_TabElements[i].worldBound;
                if (worldPos.x < rect.center.x)
                {
                    markerX = this.WorldToLocal(new Vector2(rect.xMin, 0)).x - 1f;
                    return i;
                }
                markerX = this.WorldToLocal(new Vector2(rect.xMax, 0)).x - 1f;
            }
            return m_TabElements.Count;
        }

        private void OnDragUpdated(DragUpdatedEvent e)
        {
            if (CurrentPayload() == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Move;
            InsertIndexAt(e.mousePosition, out float x);
            m_InsertMarker.style.left = x;
            m_InsertMarker.style.display = DisplayStyle.Flex;
            e.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent e)
        {
            var payload = CurrentPayload();
            m_InsertMarker.style.display = DisplayStyle.None;
            if (payload == null) return;
            DragAndDrop.AcceptDrag();
            int index = InsertIndexAt(e.mousePosition, out _);
            ClearPayload();
            e.StopPropagation();
            OnDropped?.Invoke(payload, index);
        }
    }
}
