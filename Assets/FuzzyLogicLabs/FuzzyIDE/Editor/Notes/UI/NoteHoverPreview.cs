using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>Popup previewing the target of a link under the pointer: a note, a code location, an asset or a URL.</summary>
    public class NoteHoverPreview : VisualElement
    {
        private const float Width = 400f;
        private const int MaxNoteLines = 18;
        private const int CodeContextBefore = 3;
        private const int CodeContextAfter = 9;

        private readonly Label m_Title;
        private readonly NotesIcon m_TitleIcon;
        private readonly Label m_Body;
        private readonly Image m_Thumb;

        public NoteHoverPreview()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.width = Width;
            style.maxHeight = 300;
            style.overflow = Overflow.Hidden;
            style.display = DisplayStyle.None;
            style.backgroundColor = NotesTheme.PopupBackground;
            style.borderTopWidth = 1; style.borderBottomWidth = 1; style.borderLeftWidth = 1; style.borderRightWidth = 1;
            style.borderTopColor = NotesTheme.Border; style.borderBottomColor = NotesTheme.Border;
            style.borderLeftColor = NotesTheme.Border; style.borderRightColor = NotesTheme.Border;
            style.borderTopLeftRadius = 8; style.borderTopRightRadius = 8; style.borderBottomLeftRadius = 8; style.borderBottomRightRadius = 8;
            style.paddingLeft = 12; style.paddingRight = 12; style.paddingTop = 10; style.paddingBottom = 10;

            var titleRow = new VisualElement { pickingMode = PickingMode.Ignore, style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
            m_TitleIcon = NotesIcons.Create("file-text", 16f, NotesTheme.TextMuted);
            m_TitleIcon.style.marginRight = 6;
            m_Title = new Label { pickingMode = PickingMode.Ignore, style = { flexShrink = 1, fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextNormal, whiteSpace = WhiteSpace.Normal } };
            titleRow.Add(m_TitleIcon);
            titleRow.Add(m_Title);
            m_Thumb = new Image { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.ScaleToFit, style = { width = 64, height = 64, display = DisplayStyle.None, marginBottom = 4 } };
            m_Body = new Label { pickingMode = PickingMode.Ignore, enableRichText = false, style = { fontSize = 12, whiteSpace = WhiteSpace.Normal, color = NotesTheme.TextMuted } };
            Add(titleRow);
            Add(m_Thumb);
            Add(m_Body);
        }

        /// <param name="anchor">Link rectangle in the parent's coordinates.</param>
        public void Show(string target, Rect anchor)
        {
            if (!BuildContent(target)) { Hide(); return; }

            style.display = DisplayStyle.Flex;
            float parentW = parent != null ? parent.layout.width : 800f;
            float parentH = parent != null ? parent.layout.height : 600f;

            float left = Mathf.Clamp(anchor.x, 8f, Mathf.Max(8f, parentW - Width - 8f));
            float below = anchor.yMax + 6f;
            bool fitsBelow = float.IsNaN(parentH) || below + 200f < parentH;
            style.left = left;
            style.top = fitsBelow ? below : Mathf.Max(8f, anchor.y - 206f);
        }

        public void Hide() => style.display = DisplayStyle.None;

        private void SetTitle(string icon, string text)
        {
            m_TitleIcon.SetIcon(icon);
            m_Title.text = text;
        }

        private bool BuildContent(string target)
        {
            m_Thumb.style.display = DisplayStyle.None;
            m_Body.style.unityFontDefinition = new StyleFontDefinition(StyleKeyword.Null);
            if (string.IsNullOrWhiteSpace(target)) return false;
            target = target.Trim();

            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                SetTitle("external-link", "Web link");
                m_Body.text = target + "\n\nCtrl+Click to open in the browser.";
                return true;
            }

            if (target.StartsWith("asset:", StringComparison.OrdinalIgnoreCase))
            {
                return BuildAsset(target.Substring(6).Trim());
            }

            if (NoteLinkResolver.TryParseCodeLink(target, out string file, out int line, out string symbol))
            {
                return BuildCode(file, line, symbol);
            }

            var note = NoteStore.Instance.FindNoteByLink(target);
            if (note != null) return BuildNote(note, target);

            if (target.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || target.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return BuildAsset(target);
            }

            SetTitle("file-text", target);
            m_Body.text = "No note with this name yet.";
            return true;
        }

        private bool BuildNote(NoteItem note, string target)
        {
            SetTitle("file-text", note.Title);

            var lines = MarkdownScan.NormalizeNewlines(note.Body ?? "").Split('\n');
            int start = 0;

            // [[Note#Heading]] previews from that heading
            int hash = target.IndexOf('#');
            if (hash >= 0)
            {
                string heading = target.Substring(hash + 1).Split('|')[0].Trim();
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("#") && lines[i].TrimStart('#', ' ').Trim().Equals(heading, StringComparison.OrdinalIgnoreCase))
                    {
                        start = i;
                        break;
                    }
                }
            }

            var sb = new StringBuilder();
            int taken = 0;
            for (int i = start; i < lines.Length && taken < MaxNoteLines; i++)
            {
                if (taken == 0 && lines[i].Trim().Length == 0) continue;
                sb.AppendLine(lines[i]);
                taken++;
            }
            if (start + taken < lines.Length) sb.Append("…");
            m_Body.text = sb.Length == 0 ? "(empty note)" : sb.ToString().TrimEnd();
            return true;
        }

        private bool BuildCode(string file, int line, string symbol)
        {
            string assetPath = NoteLinkResolver.ResolveCodeAssetPath(file);
            string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            SetTitle("code", assetPath + (symbol != null ? "#" + symbol : ":" + line));

            if (!File.Exists(fullPath))
            {
                m_Body.text = "File not found in the project.";
                return true;
            }

            string[] lines;
            try { lines = File.ReadAllLines(fullPath); }
            catch (IOException) { m_Body.text = "Could not read the file."; return true; }

            if (symbol != null)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"\b" + System.Text.RegularExpressions.Regex.Escape(symbol) + @"\s*[\(<]"))
                    {
                        line = i + 1;
                        break;
                    }
                }
            }

            int from = Math.Max(1, line - CodeContextBefore);
            int to = Math.Min(lines.Length, line + CodeContextAfter);
            var sb = new StringBuilder();
            for (int i = from; i <= to; i++)
            {
                sb.Append(i == line ? "▶" : " ").Append(i.ToString().PadLeft(4)).Append("  ").AppendLine(lines[i - 1].Replace("\t", "    "));
            }
            m_Body.text = sb.ToString().TrimEnd();
            m_Body.style.unityFontDefinition = MonoFont();
            return true;
        }

        private bool BuildAsset(string pathOrGuid)
        {
            string assetPath = pathOrGuid;
            if (pathOrGuid.Length == 32 && !pathOrGuid.Contains("/")) assetPath = AssetDatabase.GUIDToAssetPath(pathOrGuid);

            var obj = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadMainAssetAtPath(assetPath);
            SetTitle("box", string.IsNullOrEmpty(assetPath) ? pathOrGuid : assetPath);

            if (obj == null)
            {
                m_Body.text = "Asset not found.";
                return true;
            }

            var preview = AssetPreview.GetAssetPreview(obj) ?? AssetPreview.GetMiniThumbnail(obj);
            if (preview != null)
            {
                m_Thumb.image = preview;
                m_Thumb.style.display = DisplayStyle.Flex;
            }
            m_Body.text = obj.GetType().Name + "\nCtrl+Click to select it in the Project window.";
            return true;
        }

        private static StyleFontDefinition MonoFont()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/FuzzyLogicLabs/FuzzyIDE/Editor/Resources/Fonts/Jetbrains Mono/JetBrainsMono-Regular.ttf");
            return font != null ? new StyleFontDefinition(FontDefinition.FromFont(font)) : new StyleFontDefinition(StyleKeyword.Null);
        }
    }
}
