using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Semantic colors for Fuzzy Notes, derived from the IDE theme (<see cref="CodeIDESettings"/>) so that every
    /// preset — including Light — and custom themes look consistent. Values are computed on access, so they
    /// always reflect the current settings.
    ///
    /// Static colors live here; interaction states (hover, pressed, focus) and spacing live in Notes.uss, whose
    /// overlay variables are switched with the <c>fn-theme--dark</c> / <c>fn-theme--light</c> root class.
    /// </summary>
    public static class NotesTheme
    {
        public const string StyleSheetPath = "Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Notes/UI/Style/Notes.uss";

        public const string RootClass = "fn-root";
        private const string DarkClass = "fn-theme--dark";
        private const string LightClass = "fn-theme--light";

        private static StyleSheet s_StyleSheet;

        private static ThemeSettings T => CodeIDESettings.Current.Theme;

        public static bool IsLight => Luminance(T.Background) > 0.5f;

        // ---- backgrounds
        /// <summary>Editor surface (the note page).</summary>
        public static Color BgPrimary => Opaque(T.Background);
        /// <summary>Sidebars, ribbon, tab strip.</summary>
        public static Color BgSecondary => Shade(T.Background, IsLight ? 0.035f : 0.2f);
        /// <summary>Headers and bars that sit on the secondary background.</summary>
        public static Color BgSecondaryAlt => Shade(T.Background, IsLight ? 0.06f : 0.1f);
        /// <summary>Raised surfaces inside a page: cards, embeds, code blocks.</summary>
        public static Color BgRaised => Color.Lerp(BgPrimary, TextNormal, IsLight ? 0.035f : 0.05f);
        public static Color BgHover => WithAlpha(TextNormal, IsLight ? 0.06f : 0.075f);
        public static Color BgActive => WithAlpha(TextNormal, IsLight ? 0.1f : 0.12f);
        public static Color PopupBackground => Opaque(Color.Lerp(T.UIPopupBackground, BgPrimary, IsLight ? 0.6f : 0.2f));
        /// <summary>Dim layer behind modal overlays (palette).</summary>
        public static Color Backdrop => new Color(0f, 0f, 0f, IsLight ? 0.18f : 0.4f);

        // ---- text
        public static Color TextNormal => Opaque(T.DefaultText);
        public static Color TextMuted => Color.Lerp(TextNormal, BgPrimary, 0.32f);
        public static Color TextFaint => Color.Lerp(TextNormal, BgPrimary, 0.55f);
        public static Color TextOnAccent => Luminance(Accent) > 0.6f ? new Color(0.08f, 0.08f, 0.08f, 1f) : Color.white;

        // ---- accent & semantic
        public static Color Accent => NoteSettings.AccentColor.Length > 0 && ColorUtility.TryParseHtmlString(NoteSettings.AccentColor, out var custom) ? Opaque(custom) : Opaque(T.KeywordColor);
        public static Color AccentSoft => WithAlpha(Accent, IsLight ? 0.16f : 0.22f);
        public static Color Selection => T.SelectionHighlight;
        public static Color Success => IsLight ? new Color(0.13f, 0.55f, 0.25f, 1f) : new Color(0.35f, 0.75f, 0.45f, 1f);
        public static Color Warning => IsLight ? new Color(0.75f, 0.45f, 0.05f, 1f) : new Color(0.9f, 0.68f, 0.3f, 1f);
        public static Color Error => IsLight ? new Color(0.8f, 0.2f, 0.2f, 1f) : new Color(0.92f, 0.4f, 0.4f, 1f);
        public static Color Purple => IsLight ? new Color(0.5f, 0.3f, 0.75f, 1f) : new Color(0.68f, 0.52f, 0.92f, 1f);

        // ---- lines
        public static Color Border => WithAlpha(T.UIBorder, IsLight ? 0.9f : 0.7f);
        public static Color BorderSubtle => WithAlpha(T.UIBorder, 0.35f);

        // ---- editor surface
        public static Color CodeBackground => WithAlpha(TextNormal, IsLight ? 0.05f : 0.045f);
        public static Color SearchMatch => IsLight ? new Color(1f, 0.8f, 0.1f, 0.35f) : new Color(1f, 0.82f, 0.3f, 0.25f);
        public static Color SearchMatchCurrent => IsLight ? new Color(1f, 0.6f, 0.05f, 0.6f) : new Color(1f, 0.65f, 0.15f, 0.55f);

        /// <summary>
        /// Adds the Notes stylesheet and theme class to a window root and paints its background.
        /// Call again after the IDE theme changed.
        /// </summary>
        public static void Apply(VisualElement root, bool secondaryBackground = false)
        {
            if (s_StyleSheet == null) s_StyleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (s_StyleSheet != null && !root.styleSheets.Contains(s_StyleSheet)) root.styleSheets.Add(s_StyleSheet);

            root.AddToClassList(RootClass);
            root.EnableInClassList(LightClass, IsLight);
            root.EnableInClassList(DarkClass, !IsLight);
            root.style.backgroundColor = secondaryBackground ? BgSecondary : BgPrimary;
            root.style.color = TextNormal;
            TrackKeyboardNavigation(root);
        }

        private const string KeyboardNavClass = "fn-keyboard-nav";

        /// <summary>
        /// Focus rings only for keyboard users (like :focus-visible): Tab turns them on, using the pointer turns them off.
        /// </summary>
        private static void TrackKeyboardNavigation(VisualElement root)
        {
            root.UnregisterCallback<KeyDownEvent>(OnNavKey, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerDownEvent>(OnNavPointer, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnNavKey, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerDownEvent>(OnNavPointer, TrickleDown.TrickleDown);
        }

        private static void OnNavKey(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Tab && !e.actionKey) ((VisualElement)e.currentTarget).AddToClassList(KeyboardNavClass);
        }

        private static void OnNavPointer(PointerDownEvent e) => ((VisualElement)e.currentTarget).RemoveFromClassList(KeyboardNavClass);

        public static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c);

        public static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        private static Color Opaque(Color c) => new Color(c.r, c.g, c.b, 1f);

        /// <summary>Moves a background away from the text: darker on dark themes, slightly darker on light ones.</summary>
        private static Color Shade(Color bg, float amount) => Opaque(Color.Lerp(bg, Color.black, amount));

        private static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
    }

    public static class NotesElementExtensions
    {
        /// <summary>Adds USS classes inline: <c>new Label("x").WithClass("fn-row-label")</c>.</summary>
        public static T WithClass<T>(this T element, params string[] classes) where T : VisualElement
        {
            foreach (var c in classes) element.AddToClassList(c);
            return element;
        }

        /// <summary>Adds a child and returns the parent, for building small trees in one expression.</summary>
        public static T AddChild<T>(this T parent, VisualElement child) where T : VisualElement
        {
            parent.Add(child);
            return parent;
        }
    }
}
