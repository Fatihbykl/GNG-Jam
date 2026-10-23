using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>
    /// VS Code color themes (.json, comments allowed) to <see cref="ThemeSettings"/> and back. Workbench colors map
    /// to editor surfaces; TextMate scopes and semantic token colors map to the syntax colors.
    /// </summary>
    public static class ThemeImporter
    {
        private struct Target
        {
            public string[] Scopes;
            public Action<ThemeSettings, Color> Set;
        }

        private static readonly Target[] s_Targets =
        {
            T((t, c) => t.KeywordColor = c, "keyword", "storage.type", "storage.modifier", "keyword.control"),
            T((t, c) => t.StringColor = c, "string"),
            T((t, c) => t.CommentColor = c, "comment"),
            T((t, c) => t.NumberColor = c, "constant.numeric"),
            T((t, c) => t.TypeColor = c, "entity.name.type", "entity.name.class", "support.class", "support.type"),
            T((t, c) => { t.MethodColor = c; t.UnityMessageColor = c; }, "entity.name.function", "support.function"),
            T((t, c) => t.VariableColor = c, "variable", "variable.other", "variable.other.readwrite"),
            T((t, c) => t.ParameterColor = c, "variable.parameter"),
            T((t, c) => t.FieldColor = c, "variable.other.member", "entity.name.variable.field", "variable.other.object.property"),
            T((t, c) => t.PropertyColor = c, "variable.other.property", "support.variable.property"),
        };

        private static readonly Dictionary<string, Action<ThemeSettings, Color>> s_SemanticTokens = new Dictionary<string, Action<ThemeSettings, Color>>
        {
            { "keyword", (t, c) => t.KeywordColor = c },
            { "string", (t, c) => t.StringColor = c },
            { "comment", (t, c) => t.CommentColor = c },
            { "number", (t, c) => t.NumberColor = c },
            { "class", (t, c) => t.TypeColor = c },
            { "type", (t, c) => t.TypeColor = c },
            { "method", (t, c) => t.MethodColor = c },
            { "function", (t, c) => t.MethodColor = c },
            { "variable", (t, c) => t.VariableColor = c },
            { "parameter", (t, c) => t.ParameterColor = c },
            { "property", (t, c) => t.PropertyColor = c },
            { "field", (t, c) => t.FieldColor = c },
        };

        private static Target T(Action<ThemeSettings, Color> set, params string[] scopes) => new Target { Scopes = scopes, Set = set };

        /// <summary>Applies a VS Code theme to <paramref name="theme"/> (switching it to Custom). False with an error when unreadable.</summary>
        public static bool ImportVsCode(string json, ThemeSettings theme, out string error)
        {
            error = null;
            Dictionary<string, object> root;
            try { root = MiniJson.Parse(json) as Dictionary<string, object>; }
            catch (Exception e) { error = e.Message; return false; }
            if (root == null) { error = "Not a JSON object."; return false; }

            if (root.TryGetValue("colors", out var colorsValue) && colorsValue is Dictionary<string, object> colors)
            {
                bool Get(string key, out Color c)
                {
                    c = default;
                    return colors.TryGetValue(key, out var v) && v is string s && TryParse(s, out c);
                }

                if (Get("editor.background", out var bg)) { theme.Background = bg; theme.GutterBackground = bg; }
                if (Get("editorGutter.background", out var gutter)) theme.GutterBackground = gutter;
                if (Get("editor.foreground", out var fg)) { theme.DefaultText = fg; theme.VariableColor = fg; theme.FieldColor = fg; theme.PropertyColor = fg; theme.ParameterColor = fg; }
                if (Get("editor.lineHighlightBackground", out var line)) theme.ActiveLineBackground = Opaque(line, theme.Background);
                if (Get("editor.selectionBackground", out var selection)) { selection.a = Mathf.Min(selection.a, 0.6f); theme.SelectionHighlight = selection; }
                if (Get("editorCursor.foreground", out var caret)) theme.CaretColor = caret;
                if (Get("editorWidget.background", out var popup) || Get("editorSuggestWidget.background", out popup)) { popup.a = 0.97f; theme.UIPopupBackground = popup; }
                if (Get("editorWidget.border", out var border) || Get("panel.border", out border) || Get("editorGroup.border", out border)) theme.UIBorder = border;
                if (Get("editorInlayHint.foreground", out var inlay)) theme.InlayHintColor = inlay;
                else if (Get("editorLineNumber.foreground", out var lineNumber)) theme.InlayHintColor = lineNumber;
                if (Get("editorGutter.addedBackground", out var added)) theme.GitAdded = added;
                if (Get("editorGutter.modifiedBackground", out var modified)) theme.GitModified = modified;
                if (Get("editorGutter.deletedBackground", out var deleted)) theme.GitDeleted = deleted;
            }

            if (root.TryGetValue("tokenColors", out var tokenValue) && tokenValue is List<object> rules)
            {
                // Per target keep the most specific matching rule: longest matching prefix, then the scope closest to it.
                var best = new (int prefix, int extra)?[s_Targets.Length];
                var chosen = new Color[s_Targets.Length];
                foreach (var ruleValue in rules)
                {
                    if (!(ruleValue is Dictionary<string, object> rule)) continue;
                    if (!(rule.TryGetValue("settings", out var sv) && sv is Dictionary<string, object> settings)) continue;
                    if (!(settings.TryGetValue("foreground", out var fgv) && fgv is string fgText && TryParse(fgText, out var color))) continue;
                    foreach (var scope in Scopes(rule))
                    {
                        for (int i = 0; i < s_Targets.Length; i++)
                        {
                            foreach (var prefix in s_Targets[i].Scopes)
                            {
                                if (scope != prefix && !scope.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
                                var score = (prefix.Length, -(scope.Length - prefix.Length));
                                if (best[i] == null || score.CompareTo(best[i].Value) >= 0)
                                {
                                    best[i] = (score.Item1, score.Item2);
                                    chosen[i] = color;
                                }
                            }
                        }
                    }
                }
                for (int i = 0; i < s_Targets.Length; i++)
                    if (best[i] != null) s_Targets[i].Set(theme, chosen[i]);
            }

            if (root.TryGetValue("semanticTokenColors", out var semanticValue) && semanticValue is Dictionary<string, object> semantic)
            {
                foreach (var kvp in semantic)
                {
                    string token = kvp.Key.Split(':', '.')[0];
                    string text = kvp.Value as string ?? ((kvp.Value as Dictionary<string, object>)?.TryGetValue("foreground", out var f) == true ? f as string : null);
                    if (text != null && TryParse(text, out var color) && s_SemanticTokens.TryGetValue(token, out var set)) set(theme, color);
                }
            }

            theme.ActivePreset = ThemePreset.Custom;
            return true;
        }

        private static IEnumerable<string> Scopes(Dictionary<string, object> rule)
        {
            if (!rule.TryGetValue("scope", out var value)) yield break;
            if (value is string text)
            {
                foreach (var s in text.Split(',')) if (s.Trim().Length > 0) yield return s.Trim();
            }
            else if (value is List<object> list)
            {
                foreach (var s in list.OfType<string>()) yield return s.Trim();
            }
        }

        /// <summary>A VS Code-compatible theme file, so FuzzyIDE themes can be shared and re-imported.</summary>
        public static string ExportVsCode(ThemeSettings t, string name)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"name\": \"").Append(name.Replace("\"", "'")).Append("\",\n  \"type\": \"").Append(Luminance(t.Background) < 0.5f ? "dark" : "light").Append("\",\n");
            sb.Append("  \"colors\": {\n");
            var colors = new (string, Color)[]
            {
                ("editor.background", t.Background), ("editorGutter.background", t.GutterBackground), ("editor.foreground", t.DefaultText),
                ("editor.lineHighlightBackground", t.ActiveLineBackground), ("editor.selectionBackground", t.SelectionHighlight),
                ("editorCursor.foreground", t.CaretColor), ("editorWidget.background", t.UIPopupBackground), ("editorWidget.border", t.UIBorder),
                ("editorInlayHint.foreground", t.InlayHintColor), ("editorGutter.addedBackground", t.GitAdded),
                ("editorGutter.modifiedBackground", t.GitModified), ("editorGutter.deletedBackground", t.GitDeleted),
            };
            sb.Append(string.Join(",\n", colors.Select(c => $"    \"{c.Item1}\": \"{Hex(c.Item2)}\""))).Append("\n  },\n");
            sb.Append("  \"tokenColors\": [\n");
            var tokens = new (string, Color)[]
            {
                ("keyword", t.KeywordColor), ("string", t.StringColor), ("comment", t.CommentColor), ("constant.numeric", t.NumberColor),
                ("entity.name.type", t.TypeColor), ("entity.name.function", t.MethodColor), ("variable", t.VariableColor),
                ("variable.parameter", t.ParameterColor), ("variable.other.member", t.FieldColor), ("variable.other.property", t.PropertyColor),
            };
            sb.Append(string.Join(",\n", tokens.Select(c => $"    {{ \"scope\": \"{c.Item1}\", \"settings\": {{ \"foreground\": \"{Hex(c.Item2)}\" }} }}"))).Append("\n  ]\n}\n");
            return sb.ToString();
        }

        public static bool TryParse(string text, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim();
            if (!text.StartsWith("#")) return false;
            string hex = text.Substring(1);
            if (hex.Length == 3 || hex.Length == 4) hex = string.Concat(hex.Select(ch => new string(ch, 2)));
            if (hex.Length != 6 && hex.Length != 8) return false;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return false;
            if (hex.Length == 6) value = (value << 8) | 0xFF;
            color = new Color(((value >> 24) & 0xFF) / 255f, ((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f);
            return true;
        }

        public static string Hex(Color c)
        {
            string rgb = $"#{Byte(c.r):X2}{Byte(c.g):X2}{Byte(c.b):X2}";
            return c.a >= 0.999f ? rgb : rgb + $"{Byte(c.a):X2}";
        }

        private static int Byte(float v) => Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        // Translucent highlight colors are blended onto the background; the editor paints line backgrounds opaque.
        private static Color Opaque(Color c, Color background) => c.a >= 0.999f ? c : new Color(
            Mathf.Lerp(background.r, c.r, c.a), Mathf.Lerp(background.g, c.g, c.a), Mathf.Lerp(background.b, c.b, c.a), 1f);
    }
}
