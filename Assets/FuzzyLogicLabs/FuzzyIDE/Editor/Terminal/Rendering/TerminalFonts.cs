using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal.Rendering
{
    /// <summary>
    /// Font assets and cell metrics for the terminal grid. The cell size is measured with the same
    /// FontAsset that MeshGenerationContext.DrawText uses, so column math and glyph placement agree.
    /// In crisp mode the glyphs are rasterized with hinting at the exact device pixel size (like native
    /// terminals) instead of being drawn from a scalable SDF atlas, which looks soft at small sizes. The
    /// cell is then a whole number of device pixels, so every glyph lands on the pixel grid.
    /// </summary>
    internal sealed class TerminalFonts
    {
        private const string FontFolder = "Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Resources/Fonts/Cascadia Code/";
        private const int SdfSamplingSize = 90;

        private sealed class FontSet
        {
            public FontAsset Regular, Bold, Italic;
        }

        // Font assets are expensive to build and own atlas textures; share them for the editor session.
        // Key: device pixel size in crisp mode, 0 for the size-independent SDF set.
        private static readonly Dictionary<int, FontSet> s_sets = new Dictionary<int, FontSet>();

        public FontAsset Regular { get; private set; }
        public FontAsset Bold { get; private set; }
        public FontAsset Italic { get; private set; }
        /// <summary>Requested size in points.</summary>
        public int FontSize { get; private set; }
        /// <summary>Size to pass to DrawText; in crisp mode it maps the atlas 1:1 onto device pixels.</summary>
        public float DrawSize { get; private set; }
        public bool Crisp { get; private set; }
        public float PixelsPerPoint { get; private set; }
        public float CellWidth { get; private set; }
        public float CellHeight { get; private set; }

        /// <summary>
        /// False once the editor destroyed one of the generated materials (scene changes unload assets that are not
        /// flagged DontSave). While alive, newly added atlas pages are flagged too, since dynamic atlases grow.
        /// </summary>
        public bool EnsureAlive()
        {
            return EnsureAlive(Regular) && EnsureAlive(Bold) && EnsureAlive(Italic);
        }

        private static bool EnsureAlive(FontAsset asset)
        {
            if ((object)asset == null) return true;
            if (asset == null || asset.material == null) return false;

            Protect(asset);
            if (asset.fallbackFontAssetTable != null)
            {
                foreach (var fallback in asset.fallbackFontAssetTable)
                {
                    if ((object)fallback == null) continue;
                    if (fallback == null || fallback.material == null) return false;
                    Protect(fallback);
                }
            }
            return true;
        }

        // The material and atlas textures are separate objects from the font asset. Without DontSave they get
        // unloaded (e.g. by EditorSceneManager.NewScene when the Test Runner starts) while the asset survives, and
        // every DrawText then throws MissingReferenceException.
        private static void Protect(FontAsset asset)
        {
            asset.hideFlags = HideFlags.DontSave;
            if (asset.material != null) asset.material.hideFlags = HideFlags.DontSave;
            if (asset.atlasTextures == null) return;
            foreach (var texture in asset.atlasTextures)
            {
                if (texture != null) texture.hideFlags = HideFlags.DontSave;
            }
        }

        public FontAsset Select(bool bold, bool italic)
        {
            if (bold) return Bold ?? Regular;
            if (italic) return Italic ?? Regular;
            return Regular;
        }

        /// <summary>Loads fonts and measures the cell for <paramref name="fontSize"/>. The host must be attached to a panel.</summary>
        public static TerminalFonts Create(int fontSize, VisualElement measureHost, bool crisp)
        {
            float ppp = Mathf.Max(0.5f, EditorGUIUtility.pixelsPerPoint);
            int pixelSize = Mathf.Max(6, Mathf.RoundToInt(fontSize * ppp));

            var set = GetSet(crisp ? pixelSize : 0);
            if (crisp && set.Regular == null)
            {
                crisp = false; // no usable font for raster mode; fall back to SDF
                set = GetSet(0);
            }

            var fonts = new TerminalFonts
            {
                Regular = set.Regular,
                Bold = set.Bold,
                Italic = set.Italic,
                FontSize = fontSize,
                DrawSize = crisp ? pixelSize / ppp : fontSize,
                Crisp = crisp,
                PixelsPerPoint = ppp
            };
            fonts.Measure(measureHost);
            return fonts;
        }

        private void Measure(VisualElement host)
        {
            const int sampleLength = 100;
            string sample = new string('M', sampleLength);
            var probe = new UnityEngine.UIElements.TextElement { text = sample, enableRichText = false };
            probe.style.position = Position.Absolute;
            probe.style.visibility = Visibility.Hidden;
            probe.style.whiteSpace = WhiteSpace.Pre;
            probe.style.fontSize = DrawSize;
            probe.style.unityFontDefinition = Regular != null ? FontDefinition.FromSDFFont(Regular) : default;
            probe.style.paddingLeft = probe.style.paddingRight = probe.style.paddingTop = probe.style.paddingBottom = 0;

            Vector2 size = Vector2.zero;
            if (host != null)
            {
                host.Add(probe);
                size = probe.MeasureTextSize(sample, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
                host.Remove(probe);
            }

            float width = size.x > 0 ? size.x / sampleLength : FontSize * 0.6f;
            float height = size.y > 0 ? size.y : FontSize * 1.35f;

            // Hinted advances are whole pixels already; rounding only removes measurement noise. In SDF mode the
            // width must stay the font's own advance, because ASCII runs are laid out by the font.
            CellWidth = Crisp ? Mathf.Max(1f, Mathf.Round(width * PixelsPerPoint)) / PixelsPerPoint : width;
            CellHeight = Mathf.Ceil(height * PixelsPerPoint) / PixelsPerPoint;
        }

        private static FontSet GetSet(int pixelSize)
        {
            if (s_sets.TryGetValue(pixelSize, out var set) && set.Regular != null &&
                EnsureAlive(set.Regular) && EnsureAlive(set.Bold) && EnsureAlive(set.Italic)) return set;

            bool crisp = pixelSize > 0;
            set = new FontSet
            {
                Regular = LoadProjectFont("CascadiaCode-Regular.ttf", pixelSize) ?? CreateOsFont("Cascadia Code", "Regular", pixelSize) ?? CreateOsFont("Consolas", "Regular", pixelSize),
                Bold = LoadProjectFont("CascadiaCode-Bold.ttf", pixelSize),
                Italic = LoadProjectFont("CascadiaCode-Italic.ttf", pixelSize)
            };

            var fallbacks = new List<FontAsset>();
            foreach (var family in GetFallbackFamilies())
            {
                var asset = CreateOsFont(family, "Regular", pixelSize);
                if (asset != null) fallbacks.Add(asset);
            }

            foreach (var primary in new[] { set.Regular, set.Bold, set.Italic })
            {
                if (primary == null) continue;
                if (primary.fallbackFontAssetTable == null) primary.fallbackFontAssetTable = new List<FontAsset>();
                foreach (var fallback in fallbacks)
                {
                    if (fallback != primary && !primary.fallbackFontAssetTable.Contains(fallback))
                        primary.fallbackFontAssetTable.Add(fallback);
                }
            }

            if (crisp && set.Regular == null) return set;
            s_sets[pixelSize] = set;
            return set;
        }

        private static IEnumerable<string> GetFallbackFamilies()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    return new[] { "Consolas", "Segoe UI Symbol", "Segoe UI Emoji", "MS Gothic", "Microsoft YaHei" };
                case RuntimePlatform.OSXEditor:
                    return new[] { "Menlo", "SF Mono", "Apple Symbols", "Apple Color Emoji", "Hiragino Sans", "PingFang SC" };
                default:
                    return new[] { "DejaVu Sans Mono", "Noto Sans Mono", "Noto Sans Symbols 2", "Noto Color Emoji", "Noto Sans CJK SC" };
            }
        }

        private static FontAsset LoadProjectFont(string fileName, int pixelSize)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontFolder + fileName);
            if (font == null) return null;
            try
            {
                var asset = pixelSize > 0
                    ? FontAsset.CreateFontAsset(font, pixelSize, AtlasPadding(pixelSize), GlyphRenderMode.SMOOTH_HINTED, 1024, 1024, AtlasPopulationMode.Dynamic, true)
                    : FontAsset.CreateFontAsset(font);
                if (asset != null) Protect(asset);
                return asset;
            }
            catch
            {
                return null;
            }
        }

        private static HashSet<string> s_installedFamilies;

        private static FontAsset CreateOsFont(string family, string style, int pixelSize)
        {
            // FontAsset.CreateFontAsset logs a warning for every family that is not installed, so only
            // installed ones are requested.
            if (s_installedFamilies == null)
            {
                s_installedFamilies = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                try { s_installedFamilies.UnionWith(Font.GetOSInstalledFontNames()); } catch { }
            }
            if (!s_installedFamilies.Contains(family)) return null;

            try
            {
                FontAsset asset = null;
                if (pixelSize > 0 && s_createOsFontWithMode != null)
                {
                    asset = s_createOsFontWithMode.Invoke(null, new object[] { family, style, pixelSize, AtlasPadding(pixelSize), GlyphRenderMode.SMOOTH_HINTED }) as FontAsset;
                }
                // A fallback in SDF mode still works next to hinted primaries; the render mode is per atlas.
                if (asset == null) asset = FontAsset.CreateFontAsset(family, style, SdfSamplingSize);
                if (asset != null) Protect(asset);
                return asset;
            }
            catch
            {
                return null;
            }
        }

        // FontAsset.CreateFontAsset(familyName, styleName, pointSize, padding, renderMode) is internal in Unity 6.0.
        private static readonly System.Reflection.MethodInfo s_createOsFontWithMode = typeof(FontAsset).GetMethod(
            "CreateFontAsset",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(int), typeof(GlyphRenderMode) },
            null);

        private static int AtlasPadding(int pixelSize) => Mathf.Max(1, pixelSize / 8);
    }
}
