using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.LiveEditor;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>In-note find &amp; replace bound to a <see cref="MarkdownLiveEditor"/>.</summary>
    public class NoteFindBar : VisualElement
    {
        private readonly MarkdownLiveEditor m_Editor;
        private readonly TextField m_FindField;
        private readonly TextField m_ReplaceField;
        private readonly Button m_MatchCaseButton;
        private readonly Button m_ReplaceToggle;
        private bool m_MatchCase;
        private readonly Label m_CountLabel;
        private readonly VisualElement m_ReplaceRow;

        private List<(TextPos start, TextPos end)> m_Matches = new List<(TextPos, TextPos)>();
        private int m_Current = -1;
        private IVisualElementScheduledItem m_PendingRefresh;

        public bool IsOpen => style.display == DisplayStyle.Flex;

        public NoteFindBar(MarkdownLiveEditor editor)
        {
            m_Editor = editor;

            style.display = DisplayStyle.None;
            style.flexShrink = 0;
            style.backgroundColor = NotesTheme.BgSecondaryAlt;
            style.borderBottomWidth = 1;
            style.borderBottomColor = NotesTheme.BorderSubtle;
            style.paddingLeft = 8; style.paddingRight = 6; style.paddingTop = 5; style.paddingBottom = 5;

            var findRow = Row();
            m_FindField = new TextField { style = { flexGrow = 1, minWidth = 120 } }.WithClass("fn-input");
            m_FindField.textEdition.placeholder = "Find in note";
            m_FindField.RegisterValueChangedCallback(_ => Search(selectNearest: true));
            m_FindField.RegisterCallback<KeyDownEvent>(OnFieldKeyDown, TrickleDown.TrickleDown);
            findRow.Add(m_FindField);

            m_MatchCaseButton = NotesIcons.IconButton("text", "Match case", () => SetMatchCase(!m_MatchCase));
            m_MatchCaseButton.style.marginLeft = 4;
            findRow.Add(m_MatchCaseButton);

            m_CountLabel = new Label("") { style = { minWidth = 70, marginLeft = 4, fontSize = 12, color = NotesTheme.TextFaint, unityTextAlign = TextAnchor.MiddleCenter } };
            findRow.Add(m_CountLabel);

            findRow.Add(NotesIcons.IconButton("chevron-down", "Previous match (Shift+Enter)", () => Step(-1)).WithClass("fn-flip-y"));
            findRow.Add(NotesIcons.IconButton("chevron-down", "Next match (Enter)", () => Step(1)));
            m_ReplaceToggle = NotesIcons.IconButton("arrow-up-down", "Toggle replace", () => SetReplaceVisible(m_ReplaceRow.style.display == DisplayStyle.None));
            findRow.Add(m_ReplaceToggle);
            findRow.Add(NotesIcons.IconButton("x", "Close (Esc)", Close));
            Add(findRow);

            m_ReplaceRow = Row();
            m_ReplaceRow.style.marginTop = 4;
            m_ReplaceField = new TextField { style = { flexGrow = 1, minWidth = 120 } }.WithClass("fn-input");
            m_ReplaceField.textEdition.placeholder = "Replace with";
            m_ReplaceField.RegisterCallback<KeyDownEvent>(OnFieldKeyDown, TrickleDown.TrickleDown);
            m_ReplaceRow.Add(m_ReplaceField);
            m_ReplaceRow.Add(TextButton("Replace", "Replace current match", ReplaceCurrent));
            m_ReplaceRow.Add(TextButton("Replace all", "Replace all matches", ReplaceAll));
            Add(m_ReplaceRow);
            SetReplaceVisible(false);
        }

        public void Open(bool withReplace = false)
        {
            style.display = DisplayStyle.Flex;
            SetReplaceVisible(withReplace || m_ReplaceRow.style.display == DisplayStyle.Flex);

            string selected = m_Editor.GetSelectedText();
            if (!string.IsNullOrEmpty(selected) && selected.IndexOf('\n') < 0) m_FindField.SetValueWithoutNotify(selected);

            Search(selectNearest: false);
            m_FindField.Focus();
            m_FindField.SelectAll();
        }

        public void Close()
        {
            if (!IsOpen) return;
            style.display = DisplayStyle.None;
            m_Matches.Clear();
            m_Current = -1;
            m_Editor.ClearSearch();
            m_Editor.FocusEditor();
        }

        /// <summary>Re-runs the search after the document changed (debounced).</summary>
        public void RequestRefresh()
        {
            if (!IsOpen) return;
            m_PendingRefresh?.Pause();
            m_PendingRefresh = schedule.Execute(() => Search(selectNearest: false)).StartingIn(150);
        }

        private void Search(bool selectNearest)
        {
            m_Matches = m_Editor.FindAll(m_FindField.value, m_MatchCase);
            m_Current = -1;

            if (m_Matches.Count > 0)
            {
                var caret = m_Editor.HasSelection ? Min(m_Editor.Caret, m_Editor.Anchor) : m_Editor.Caret;
                m_Current = 0;
                for (int i = 0; i < m_Matches.Count; i++)
                {
                    if (m_Matches[i].start >= caret) { m_Current = i; break; }
                }
            }

            m_Editor.SetSearchMatches(m_Matches, m_Current);
            if (selectNearest && m_Current >= 0) m_Editor.Select(m_Matches[m_Current].start, m_Matches[m_Current].end);
            UpdateCount();
        }

        private void Step(int dir)
        {
            if (m_Matches.Count == 0) return;
            m_Current = (m_Current + dir + m_Matches.Count) % m_Matches.Count;
            m_Editor.SetSearchMatches(m_Matches, m_Current);
            m_Editor.Select(m_Matches[m_Current].start, m_Matches[m_Current].end);
            UpdateCount();
        }

        private void ReplaceCurrent()
        {
            if (m_Current < 0 || m_Current >= m_Matches.Count) return;
            var (start, end) = m_Matches[m_Current];
            m_Editor.ReplaceRange(start, end, m_ReplaceField.value ?? "");
            Search(selectNearest: true);
        }

        private void ReplaceAll()
        {
            string query = m_FindField.value;
            if (string.IsNullOrEmpty(query) || m_Matches.Count == 0) return;

            var options = m_MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            string replacement = (m_ReplaceField.value ?? "").Replace("$", "$$");
            string replaced = Regex.Replace(m_Editor.Text, Regex.Escape(query), replacement, options);
            m_Editor.ReplaceAllText(replaced);
            Search(selectNearest: false);
        }

        private void UpdateCount()
        {
            if (string.IsNullOrEmpty(m_FindField.value)) m_CountLabel.text = "";
            else if (m_Matches.Count == 0) m_CountLabel.text = "No results";
            else m_CountLabel.text = $"{m_Current + 1} / {m_Matches.Count}";
        }

        private void OnFieldKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                if (evt.target == m_ReplaceField || m_ReplaceField.Contains(evt.target as VisualElement)) ReplaceCurrent();
                else Step(evt.shiftKey ? -1 : 1);
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                Close();
                evt.StopPropagation();
            }
        }

        private void SetReplaceVisible(bool visible)
        {
            m_ReplaceRow.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            NotesIcons.SetToggled(m_ReplaceToggle, visible);
        }

        private void SetMatchCase(bool on)
        {
            m_MatchCase = on;
            NotesIcons.SetToggled(m_MatchCaseButton, on);
            Search(selectNearest: true);
        }

        private static TextPos Min(TextPos a, TextPos b) => a < b ? a : b;

        private static VisualElement Row() => new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

        private static Button TextButton(string text, string tooltip, Action onClick) =>
            new Button(onClick) { text = text, tooltip = tooltip, style = { marginLeft = 4, color = NotesTheme.TextNormal } }.WithClass("fn-btn");
    }
}
