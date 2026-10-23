using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    /// <summary>
    /// Places floating popups (hover info, diagnostics, completion, git peek) next to an anchor such as the hovered
    /// word or the caret line. The popup goes above or below the anchor, whichever side fits (shrinking it to the
    /// available space if neither does), so it never covers the anchor itself, and it is kept inside its container.
    /// Coordinates are converted through world space, so it works whichever element the popup is parented to.
    /// </summary>
    public static class PopupPlacement
    {
        public const float Gap = 4f;
        public const float Margin = 4f;

        private sealed class Request
        {
            public Rect AnchorWorld;
            public bool PreferAbove;
            public float MinHeight;
        }

        private static readonly Dictionary<VisualElement, Request> s_Requests = new Dictionary<VisualElement, Request>();

        /// <summary>Shows <paramref name="popup"/> next to <paramref name="anchorWorld"/> and keeps it there as its size settles.</summary>
        public static void Show(VisualElement popup, Rect anchorWorld, bool preferAbove, float minHeight = 40f)
        {
            if (popup == null) return;
            if (!s_Requests.ContainsKey(popup))
            {
                // Content (and therefore size) is usually set right before showing; re-place once layout knows it.
                popup.RegisterCallback<GeometryChangedEvent>(e => Apply(popup));
            }
            s_Requests[popup] = new Request { AnchorWorld = anchorWorld, PreferAbove = preferAbove, MinHeight = minHeight };
            popup.style.bottom = StyleKeyword.Null;
            popup.style.right = StyleKeyword.Null;
            popup.style.display = DisplayStyle.Flex;
            popup.BringToFront();
            Apply(popup);
        }

        /// <summary>Anchor for the text between two x positions of one line, from coordinates local to <paramref name="owner"/>.</summary>
        public static Rect LineAnchor(VisualElement owner, float xStart, float xEnd, float lineTop, float lineHeight)
        {
            return owner.LocalToWorld(new Rect(xStart, lineTop, Mathf.Max(1f, xEnd - xStart), lineHeight));
        }

        private static void Apply(VisualElement popup)
        {
            if (popup.style.display == DisplayStyle.None || !s_Requests.TryGetValue(popup, out var request)) return;
            var container = popup.parent;
            if (container == null || float.IsNaN(container.layout.width) || container.layout.width <= 0) return;

            var bounds = new Rect(0, 0, container.layout.width, container.layout.height);
            var anchor = container.WorldToLocal(request.AnchorWorld);

            float width = Valid(popup.layout.width) ? popup.layout.width : Mathf.Max(popup.resolvedStyle.minWidth.value, 200f);
            float height = Valid(popup.layout.height) ? popup.layout.height : request.MinHeight;

            float spaceAbove = anchor.yMin - bounds.yMin - Gap - Margin;
            float spaceBelow = bounds.yMax - anchor.yMax - Gap - Margin;
            bool above = request.PreferAbove
                ? height <= spaceAbove || (height > spaceBelow && spaceAbove >= spaceBelow)
                : !(height <= spaceBelow || (height > spaceAbove && spaceBelow >= spaceAbove));

            // Never taller than the chosen side: the anchor line stays visible and clickable.
            float available = Mathf.Max(request.MinHeight, above ? spaceAbove : spaceBelow);
            SetIfChanged(popup.style.maxHeight, available, v => popup.style.maxHeight = v);
            height = Mathf.Min(height, available);

            float top = above ? anchor.yMin - Gap - height : anchor.yMax + Gap;
            top = Mathf.Clamp(top, bounds.yMin + Margin, Mathf.Max(bounds.yMin + Margin, bounds.yMax - Margin - height));
            float left = Mathf.Clamp(anchor.xMin, bounds.xMin + Margin, Mathf.Max(bounds.xMin + Margin, bounds.xMax - Margin - width));

            SetIfChanged(popup.style.top, top, v => popup.style.top = v);
            SetIfChanged(popup.style.left, left, v => popup.style.left = v);
        }

        private static bool Valid(float v) => !float.IsNaN(v) && v > 1f;

        // Re-assigning an unchanged value would trigger another geometry pass.
        private static void SetIfChanged(StyleLength current, float value, System.Action<float> set)
        {
            if (current.keyword == StyleKeyword.Undefined && Mathf.Abs(current.value.value - value) < 0.5f) return;
            set(value);
        }
    }
}
