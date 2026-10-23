using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    public sealed class PaletteItem
    {
        public string Title;
        public string Subtitle;
        public string Icon;            // Lucide icon name (NotesIcons), or literal text
        public string Hint;            // right-aligned (category, "recent")
        public string Shortcut;        // right-aligned key badge
        public Action OnChoose;
        public int Score;
        public List<int> Matches;      // highlighted title character indices
        public string Section;         // items with a new section get a header above them
        public Func<string> Preview;   // text for the preview column (quick switcher: the note's first lines)
    }

    /// <summary>
    /// Modal overlay used for the quick switcher (Ctrl+O), the command palette (Ctrl+P), pickers
    /// (templates, move-to-folder) and one-line prompts. The provider re-ranks items on every keystroke;
    /// items can be grouped under section headers and show a preview of the selected item.
    /// </summary>
    public class NotePalette : VisualElement
    {
        private const int MaxItems = 60;
        private const float ListWidth = 620f;
        private const float PreviewWidth = 320f;
        private const int PreviewMaxChars = 1500;

        private readonly VisualElement m_Panel;
        private readonly TextField m_Input;
        private readonly ScrollView m_List;
        private readonly ScrollView m_Preview;
        private readonly Label m_PreviewText;
        private readonly Label m_Footer;
        private readonly List<VisualElement> m_RowElements = new List<VisualElement>();
        private bool m_Prompt;
        private bool m_Embedded;

        private Func<string, List<PaletteItem>> m_Provider;
        private Action<string, bool> m_OnSubmitText;
        private List<PaletteItem> m_Items = new List<PaletteItem>();
        private int m_Selected;
        private VisualElement m_ReturnFocus;

        public bool IsOpen => style.display == DisplayStyle.Flex;

        /// <summary>Raised after the palette closes (chosen, cancelled or dismissed).</summary>
        public event Action Closed;

        public NotePalette()
        {
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            style.backgroundColor = NotesTheme.Backdrop;
            style.display = DisplayStyle.None;
            style.alignItems = Align.Center;
            RegisterCallback<PointerDownEvent>(e => { if (e.target == this) Close(); });

            m_Panel = new VisualElement
            {
                style =
                {
                    marginTop = 60,
                    width = Length.Percent(80),
                    maxWidth = 620,
                    maxHeight = Length.Percent(75),
                    backgroundColor = NotesTheme.PopupBackground,
                    borderTopColor = NotesTheme.Border, borderBottomColor = NotesTheme.Border, borderLeftColor = NotesTheme.Border, borderRightColor = NotesTheme.Border,
                    paddingTop = 8, paddingBottom = 6, paddingLeft = 8, paddingRight = 8
                }
            };
            m_Panel.AddToClassList("fn-popup");

            m_Input = new TextField { style = { marginBottom = 6 } };
            m_Input.AddToClassList("fn-input");
            m_Input.RegisterValueChangedCallback(_ => Rebuild());
            m_Input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            m_Panel.Add(m_Input);

            var body = new VisualElement { style = { flexDirection = FlexDirection.Row, flexShrink = 1, minHeight = 0 } };
            m_List = new ScrollView { style = { flexGrow = 1, flexShrink = 1 } };
            body.Add(m_List);
            m_Preview = new ScrollView
            {
                style =
                {
                    display = DisplayStyle.None, width = PreviewWidth, flexShrink = 0, marginLeft = 8, paddingLeft = 10, paddingRight = 6,
                    borderLeftWidth = 1, borderLeftColor = NotesTheme.BorderSubtle
                }
            };
            m_PreviewText = new Label { enableRichText = false, style = { fontSize = 12, color = NotesTheme.TextMuted, whiteSpace = WhiteSpace.Normal } };
            m_Preview.Add(m_PreviewText);
            body.Add(m_Preview);
            m_Panel.Add(body);

            m_Footer = new Label { style = { fontSize = 11, color = NotesTheme.TextFaint, marginTop = 6, paddingLeft = 4 } };
            m_Panel.Add(m_Footer);

            Add(m_Panel);
        }

        /// <summary>Fills the host instead of floating over it (used by <see cref="NotePickerWindow"/>).</summary>
        public void SetEmbedded()
        {
            m_Embedded = true;
            style.backgroundColor = Color.clear;
            m_Panel.style.marginTop = 0;
            m_Panel.style.width = Length.Percent(100);
            m_Panel.style.maxWidth = StyleKeyword.None;
            m_Panel.style.maxHeight = Length.Percent(100);
            m_Panel.style.flexGrow = 1;
            m_Panel.style.borderTopLeftRadius = 0; m_Panel.style.borderTopRightRadius = 0;
            m_Panel.style.borderBottomLeftRadius = 0; m_Panel.style.borderBottomRightRadius = 0;
        }

        /// <param name="placeholder">Hint shown in the empty input.</param>
        /// <param name="provider">Returns ranked items for the current query.</param>
        /// <param name="onSubmitText">Enter with no selectable item, or Shift+Enter: (query, forced). Null disables.</param>
        public void Open(string placeholder, string footer, Func<string, List<PaletteItem>> provider, Action<string, bool> onSubmitText = null, string initialQuery = "")
        {
            m_Prompt = false;
            Show(placeholder, footer, provider, onSubmitText, initialQuery);
        }

        /// <summary>
        /// One-line input without a list (replaces modal input dialogs): Enter submits the trimmed text, Esc cancels.
        /// </summary>
        public void OpenPrompt(string placeholder, string footer, Action<string> onSubmit, string initialText = "")
        {
            m_Prompt = true;
            Show(placeholder, footer ?? "Enter confirm · Esc cancel", _ => new List<PaletteItem>(), (text, _) => onSubmit(text), initialText);
        }

        private void Show(string placeholder, string footer, Func<string, List<PaletteItem>> provider, Action<string, bool> onSubmitText, string initialQuery)
        {
            m_Provider = provider;
            m_OnSubmitText = onSubmitText;
            if (!IsOpen) m_ReturnFocus = panel?.focusController?.focusedElement as VisualElement;

            m_Input.textEdition.placeholder = placeholder;
            m_Footer.text = footer ?? "";
            m_Input.SetValueWithoutNotify(initialQuery ?? "");
            style.display = DisplayStyle.Flex;
            BringToFront();
            Rebuild();
            schedule.Execute(() =>
            {
                m_Input.Focus();
                // a prefix such as "#" is meant to be typed after; a full value is meant to be replaced
                string value = m_Input.value ?? "";
                if (value.Length <= 1) m_Input.SelectRange(value.Length, value.Length);
                else m_Input.SelectAll();
            });
        }

        public void Close()
        {
            if (!IsOpen) return;
            style.display = DisplayStyle.None;
            m_Items.Clear();
            m_List.Clear();
            m_ReturnFocus?.Focus();
            Closed?.Invoke();
        }

        private void Rebuild()
        {
            m_Items = m_Provider != null ? m_Provider(m_Input.value ?? "") : new List<PaletteItem>();
            if (m_Items.Count > MaxItems) m_Items = m_Items.GetRange(0, MaxItems);
            m_Selected = 0;
            m_List.Clear();
            m_RowElements.Clear();
            m_List.style.display = m_Prompt ? DisplayStyle.None : DisplayStyle.Flex;

            string section = null;
            for (int i = 0; i < m_Items.Count; i++)
            {
                int index = i;
                var item = m_Items[i];
                if (!string.IsNullOrEmpty(item.Section) && item.Section != section)
                    m_List.Add(new Label(item.Section) { pickingMode = PickingMode.Ignore, style = { color = NotesTheme.TextFaint } }.WithClass("fn-popup-section"));
                section = item.Section;

                var row = new VisualElement();
                row.AddToClassList("fn-popup-item");
                row.Add(IconElement(item.Icon));

                var text = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
                text.Add(new Label(Highlight(item.Title ?? "", item.Matches)) { enableRichText = true, style = { fontSize = 13, color = NotesTheme.TextNormal, whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } });
                if (!string.IsNullOrEmpty(item.Subtitle))
                    text.Add(new Label(item.Subtitle) { enableRichText = false, style = { fontSize = 11, color = NotesTheme.TextFaint, whiteSpace = WhiteSpace.NoWrap, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } });
                row.Add(text);

                if (!string.IsNullOrEmpty(item.Hint))
                    row.Add(new Label(item.Hint) { style = { fontSize = 11, color = NotesTheme.TextFaint, marginLeft = 8 } });
                if (!string.IsNullOrEmpty(item.Shortcut))
                    row.Add(new Label(item.Shortcut) { style = { color = NotesTheme.TextMuted } }.WithClass("fn-kbd"));

                row.RegisterCallback<PointerEnterEvent>(_ => Select(index, scroll: false));
                row.RegisterCallback<ClickEvent>(_ => Choose(index));
                m_List.Add(row);
                m_RowElements.Add(row);
            }

            if (m_Items.Count == 0 && !m_Prompt)
            {
                string q = m_Input.value ?? "";
                m_List.Add(new Label(m_OnSubmitText != null && q.Trim().Length > 0 ? $"Press Enter to create “{q.Trim()}”" : "No matches")
                {
                    style = { color = NotesTheme.TextFaint }
                }.WithClass("fn-empty"));
            }
            Select(0, scroll: false);
            if (m_Items.Count == 0) UpdatePreview();
        }

        private void Select(int index, bool scroll)
        {
            if (m_Items.Count == 0) return;
            m_Selected = Mathf.Clamp(index, 0, m_Items.Count - 1);
            for (int i = 0; i < m_RowElements.Count; i++)
                m_RowElements[i].style.backgroundColor = i == m_Selected ? NotesTheme.BgActive : Color.clear;
            if (scroll) m_List.ScrollTo(m_RowElements[m_Selected]);
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            var preview = m_Selected < m_Items.Count ? m_Items[m_Selected].Preview : null;
            string text = preview?.Invoke();
            bool show = !string.IsNullOrWhiteSpace(text);
            m_Preview.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!m_Embedded) m_Panel.style.maxWidth = show ? ListWidth + PreviewWidth : ListWidth;
            if (!show) return;
            m_PreviewText.text = text.Length > PreviewMaxChars ? text.Substring(0, PreviewMaxChars) + "…" : text;
            m_Preview.scrollOffset = Vector2.zero;
        }

        private void Choose(int index)
        {
            if (index < 0 || index >= m_Items.Count) return;
            var action = m_Items[index].OnChoose;
            Close();
            action?.Invoke();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.DownArrow: Select(m_Selected + 1, true); break;
                case KeyCode.UpArrow: Select(m_Selected - 1, true); break;
                case KeyCode.PageDown: Select(m_Selected + 8, true); break;
                case KeyCode.PageUp: Select(m_Selected - 8, true); break;
                case KeyCode.Escape: Close(); break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    string query = (m_Input.value ?? "").Trim();
                    if (m_Prompt && query.Length == 0) break;
                    if (m_OnSubmitText != null && query.Length > 0 && (m_Prompt || evt.shiftKey || m_Items.Count == 0))
                    {
                        var submit = m_OnSubmitText;
                        Close();
                        submit(query, evt.shiftKey);
                    }
                    else Choose(m_Selected);
                    break;
                default:
                    return;
            }
            evt.StopPropagation();
        }

        /// <summary>Lucide icon when the name is known, otherwise the text itself (emoji, symbols).</summary>
        internal static VisualElement IconElement(string icon)
        {
            var box = new VisualElement { pickingMode = PickingMode.Ignore, style = { width = 24, flexShrink = 0, alignItems = Align.FlexStart, justifyContent = Justify.Center } };
            if (NotesIcons.Exists(icon)) box.Add(NotesIcons.Create(icon, 16f, NotesTheme.TextMuted));
            else box.Add(new Label(icon ?? "") { pickingMode = PickingMode.Ignore, style = { fontSize = 13, color = NotesTheme.TextMuted } });
            return box;
        }

        private static string Highlight(string text, List<int> matches)
        {
            if (matches == null || matches.Count == 0) return "<noparse>" + text + "</noparse>";
            string color = "#" + ColorUtility.ToHtmlStringRGB(NotesTheme.Accent);
            var set = new HashSet<int>(matches);
            var sb = new StringBuilder();
            var run = new StringBuilder();
            bool runMatched = false;

            void Flush()
            {
                if (run.Length == 0) return;
                string part = "<noparse>" + run + "</noparse>";
                sb.Append(runMatched ? $"<b><color={color}>{part}</color></b>" : part);
                run.Clear();
            }

            for (int i = 0; i < text.Length; i++)
            {
                bool m = set.Contains(i);
                if (m != runMatched) { Flush(); runMatched = m; }
                run.Append(text[i]);
            }
            Flush();
            return sb.ToString();
        }
    }
}
