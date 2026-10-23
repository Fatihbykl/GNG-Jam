using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    public enum NoteToastKind { Info, Success, Warning, Error }

    /// <summary>
    /// Short notices in the bottom-right corner of the Fuzzy Notes window (Obsidian's <c>Notice</c>), optionally with
    /// an action such as "Undo". They fade out on their own; hovering keeps them. Without an open notes window the
    /// message falls back to the focused window's notification.
    /// </summary>
    public static class NoteToast
    {
        public const int DefaultDurationMs = 4000;
        private const int MaxVisible = 4;

        private static VisualElement s_Stack;

        /// <summary>Makes <paramref name="root"/> (the notes window) the place where toasts appear.</summary>
        public static void Attach(VisualElement root)
        {
            s_Stack?.RemoveFromHierarchy();
            s_Stack = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute, right = 16, bottom = 16, maxWidth = 360,
                    flexDirection = FlexDirection.ColumnReverse, alignItems = Align.FlexEnd
                }
            };
            root.Add(s_Stack);
        }

        public static void Show(string message, NoteToastKind kind = NoteToastKind.Info, string actionLabel = null, Action action = null, int durationMs = DefaultDurationMs)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (s_Stack == null || s_Stack.panel == null)
            {
                var window = EditorWindow.focusedWindow ?? EditorWindow.mouseOverWindow;
                if (window != null) window.ShowNotification(new GUIContent(message));
                else Debug.Log("[FuzzyNotes] " + message);
                return;
            }

            s_Stack.BringToFront();
            while (s_Stack.childCount >= MaxVisible) s_Stack[0].RemoveFromHierarchy();
            s_Stack.Add(Build(message, kind, actionLabel, action, durationMs));
        }

        private static VisualElement Build(string message, NoteToastKind kind, string actionLabel, Action action, int durationMs)
        {
            var toast = new VisualElement().WithClass("fn-toast");
            toast.style.backgroundColor = NotesTheme.PopupBackground;
            toast.style.borderTopColor = toast.style.borderBottomColor = toast.style.borderLeftColor = toast.style.borderRightColor = NotesTheme.Border;

            Color tint = kind == NoteToastKind.Success ? NotesTheme.Success : kind == NoteToastKind.Warning ? NotesTheme.Warning : kind == NoteToastKind.Error ? NotesTheme.Error : NotesTheme.Accent;
            string icon = kind == NoteToastKind.Success ? "check" : kind == NoteToastKind.Info ? "info" : "triangle-alert";
            var glyph = NotesIcons.Create(icon, 15f, tint);
            glyph.style.marginRight = 8;
            toast.Add(glyph);

            toast.Add(new Label(message) { style = { flexShrink = 1, fontSize = 12, color = NotesTheme.TextNormal, whiteSpace = WhiteSpace.Normal } });

            IVisualElementScheduledItem timer = null;
            void Dismiss()
            {
                timer?.Pause();
                toast.AddToClassList("fn-toast--hidden");
                toast.schedule.Execute(toast.RemoveFromHierarchy).StartingIn(180);
            }

            if (actionLabel != null && action != null)
            {
                var button = new Button(() => { Dismiss(); action(); }) { text = actionLabel, style = { marginLeft = 10, color = NotesTheme.Accent, unityFontStyleAndWeight = FontStyle.Bold } }.WithClass("fn-btn");
                toast.Add(button);
            }

            var close = NotesIcons.IconButton("x", "Dismiss", Dismiss, 12f);
            close.AddToClassList("fn-icon-btn--small");
            close.style.marginLeft = 4;
            toast.Add(close);

            void StartTimer()
            {
                timer?.Pause();
                timer = toast.schedule.Execute(Dismiss).StartingIn(durationMs);
            }
            toast.RegisterCallback<PointerEnterEvent>(_ => timer?.Pause());
            toast.RegisterCallback<PointerLeaveEvent>(_ => StartTimer());
            toast.RegisterCallback<AttachToPanelEvent>(_ => StartTimer());
            return toast;
        }
    }
}
