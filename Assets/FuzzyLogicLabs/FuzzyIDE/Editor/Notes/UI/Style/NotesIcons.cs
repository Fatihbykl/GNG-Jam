using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Lucide line icons (the set Obsidian uses), drawn once per name into a <see cref="VectorImage"/> with
    /// <see cref="Painter2D"/>. Icons are white and tinted through the background tint, so they stay crisp at
    /// any size/DPI and follow the theme. Path data lives in the generated <see cref="NotesIconData"/>.
    /// </summary>
    public static class NotesIcons
    {
        public const float DefaultSize = 16f;
        private const float Canvas = 24f;
        private const float StrokeWidth = 1.75f; // Obsidian's --icon-stroke

        private static readonly Dictionary<string, VectorImage> s_Cache = new Dictionary<string, VectorImage>();

        static NotesIcons()
        {
            // HideAndDontSave objects outlive the domain; free them before the cache that owns them is lost.
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var image in s_Cache.Values)
                    if (image != null) UnityEngine.Object.DestroyImmediate(image);
                s_Cache.Clear();
            };
        }

        public static bool Exists(string name) => name != null && NotesIconData.Paths.ContainsKey(name);

        /// <summary>The icon as a vector image, or null for unknown names.</summary>
        public static VectorImage Get(string name)
        {
            if (!Exists(name)) return null;
            if (s_Cache.TryGetValue(name, out var cached) && cached != null) return cached;

            var image = ScriptableObject.CreateInstance<VectorImage>();
            image.hideFlags = HideFlags.HideAndDontSave;
            using (var painter = new Painter2D())
            {
                // Transparent full-canvas fill: the saved image is sized to its geometry, and every icon must
                // keep Lucide's 24x24 frame so that icons line up.
                painter.fillColor = Color.clear;
                painter.BeginPath();
                painter.MoveTo(Vector2.zero);
                painter.LineTo(new Vector2(Canvas, 0f));
                painter.LineTo(new Vector2(Canvas, Canvas));
                painter.LineTo(new Vector2(0f, Canvas));
                painter.ClosePath();
                painter.Fill();

                painter.strokeColor = Color.white;
                painter.lineWidth = StrokeWidth;
                painter.lineCap = LineCap.Round;
                painter.lineJoin = LineJoin.Round;
                painter.BeginPath();
                Trace(painter, NotesIconData.Paths[name]);
                painter.Stroke();

                painter.SaveToVectorImage(image);
            }
            s_Cache[name] = image;
            return image;
        }

        /// <summary>A non-interactive icon element. <paramref name="color"/> defaults to the normal text color.</summary>
        public static NotesIcon Create(string name, float size = DefaultSize, Color? color = null) => new NotesIcon(name, size, color);

        /// <summary>
        /// Borderless icon button (class <c>fn-icon-btn</c>): muted at rest, full strength on hover, like
        /// Obsidian's clickable icons.
        /// </summary>
        public static Button IconButton(string icon, string tooltip, Action onClick, float size = DefaultSize)
        {
            var button = new Button(onClick) { tooltip = tooltip };
            button.AddToClassList("fn-icon-btn");
            button.Add(Create(icon, size));
            return button;
        }

        /// <summary>Sets the "on" state of a toggle-style icon button (accent tint + background).</summary>
        public static void SetToggled(Button button, bool on)
        {
            button.EnableInClassList("fn-icon-btn--on", on);
            if (button.childCount > 0 && button[0] is NotesIcon icon) icon.SetColor(on ? NotesTheme.Accent : NotesTheme.TextNormal);
        }

        private static void Trace(Painter2D painter, string data)
        {
            int i = 0;
            char cmd = 'M';
            while (i < data.Length)
            {
                char c = data[i];
                if (char.IsLetter(c)) { cmd = c; i++; }
                switch (cmd)
                {
                    case 'M': painter.MoveTo(Point(data, ref i)); break;
                    case 'L': painter.LineTo(Point(data, ref i)); break;
                    case 'C':
                        var c1 = Point(data, ref i);
                        var c2 = Point(data, ref i);
                        painter.BezierCurveTo(c1, c2, Point(data, ref i));
                        break;
                    case 'Z': painter.ClosePath(); cmd = 'M'; break;
                    default: i++; break;
                }
                while (i < data.Length && data[i] == ' ') i++;
            }
        }

        private static Vector2 Point(string data, ref int i)
        {
            float x = Number(data, ref i);
            float y = Number(data, ref i);
            return new Vector2(x, y);
        }

        private static float Number(string data, ref int i)
        {
            while (i < data.Length && data[i] == ' ') i++;
            int start = i;
            if (i < data.Length && (data[i] == '-' || data[i] == '+')) i++;
            while (i < data.Length && (char.IsDigit(data[i]) || data[i] == '.' || data[i] == 'e')) i++;
            return float.Parse(data.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A Lucide icon element; see <see cref="NotesIcons"/>.</summary>
    public class NotesIcon : VisualElement
    {
        public NotesIcon(string name, float size = NotesIcons.DefaultSize, Color? color = null)
        {
            AddToClassList("fn-icon");
            pickingMode = PickingMode.Ignore;
            style.width = size;
            style.height = size;
            style.flexShrink = 0;
            SetIcon(name);
            SetColor(color ?? NotesTheme.TextNormal);
        }

        public void SetIcon(string name)
        {
            var image = NotesIcons.Get(name);
            style.backgroundImage = image != null ? new StyleBackground(image) : new StyleBackground(StyleKeyword.None);
        }

        public void SetColor(Color color) => style.unityBackgroundImageTintColor = color;
    }
}
