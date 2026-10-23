using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    public enum AutocompleteMode
    {
        WikiLink,   // [[
        Tag,        // #
        Slash       // /
    }

    public class AutocompleteCandidate
    {
        public string Title;
        public string Subtitle;
        public string Icon;
        public string InsertText;
        public string Category;

        public AutocompleteCandidate(string title, string subtitle, string icon, string insertText, string category = "")
        {
            Title = title;
            Subtitle = subtitle;
            Icon = icon;
            InsertText = insertText;
            Category = category;
        }
    }

    public class NoteAutocompletePopup : VisualElement
    {
        public event Action<AutocompleteCandidate> OnCandidateSelected;
        public event Action OnDismissed;

        public AutocompleteMode Mode { get; private set; }
        public bool IsOpen => style.display == DisplayStyle.Flex;

        private readonly ScrollView m_ScrollView;
        private readonly Label m_HeaderLabel;
        private readonly List<AutocompleteCandidate> m_AllCandidates = new List<AutocompleteCandidate>();
        private readonly List<AutocompleteCandidate> m_FilteredCandidates = new List<AutocompleteCandidate>();
        private int m_SelectedIndex = 0;

        private static List<AutocompleteCandidate> s_CachedProjectScripts;
        private static double s_LastScriptCacheTime = 0;

        public NoteAutocompletePopup()
        {
            style.position = Position.Absolute;
            style.width = 320;
            style.maxHeight = 280;
            AddToClassList("fn-popup");
            style.backgroundColor = NotesTheme.PopupBackground;
            style.borderTopColor = NotesTheme.Border;
            style.borderBottomColor = NotesTheme.Border;
            style.borderLeftColor = NotesTheme.Border;
            style.borderRightColor = NotesTheme.Border;
            style.paddingTop = 4; style.paddingBottom = 4;
            style.paddingLeft = 4; style.paddingRight = 4;
            style.display = DisplayStyle.None;

            m_HeaderLabel = new Label("Suggestions")
            {
                style = { color = NotesTheme.TextFaint, paddingLeft = 8, paddingTop = 4, paddingBottom = 4 }
            }.WithClass("fn-section-title");
            Add(m_HeaderLabel);

            m_ScrollView = new ScrollView
            {
                style = { flexGrow = 1, marginTop = 2 }
            };
            Add(m_ScrollView);
        }

        public void OpenForWikiLink(string query, Vector2 position)
        {
            Mode = AutocompleteMode.WikiLink;
            m_HeaderLabel.text = "Link a note, script or asset";
            BuildWikiLinkCandidates();
            FilterAndDisplay(query, position);
        }

        public void OpenForTag(string query, Vector2 position)
        {
            Mode = AutocompleteMode.Tag;
            m_HeaderLabel.text = "Tags";
            BuildTagCandidates();
            FilterAndDisplay(query, position);
        }

        public void OpenForSlash(string query, Vector2 position)
        {
            Mode = AutocompleteMode.Slash;
            m_HeaderLabel.text = "Insert";
            BuildSlashCandidates();
            FilterAndDisplay(query, position);
        }

        public void UpdateFilter(string query)
        {
            if (!IsOpen) return;
            FilterAndDisplay(query, layout.position);
        }

        public void Close()
        {
            style.display = DisplayStyle.None;
            OnDismissed?.Invoke();
        }

        public bool HandleKeyDown(KeyDownEvent evt)
        {
            if (!IsOpen) return false;

            if (evt.keyCode == KeyCode.DownArrow)
            {
                m_SelectedIndex = (m_SelectedIndex + 1) % Mathf.Max(1, m_FilteredCandidates.Count);
                RefreshHighlight();
                evt.StopPropagation();
                return true;
            }

            if (evt.keyCode == KeyCode.UpArrow)
            {
                m_SelectedIndex = (m_SelectedIndex - 1 + m_FilteredCandidates.Count) % Mathf.Max(1, m_FilteredCandidates.Count);
                RefreshHighlight();
                evt.StopPropagation();
                return true;
            }

            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.Tab)
            {
                if (m_FilteredCandidates.Count > 0 && m_SelectedIndex >= 0 && m_SelectedIndex < m_FilteredCandidates.Count)
                {
                    ConfirmCandidate(m_FilteredCandidates[m_SelectedIndex]);
                    evt.StopPropagation();
                    return true;
                }
            }

            if (evt.keyCode == KeyCode.Escape)
            {
                Close();
                evt.StopPropagation();
                return true;
            }

            return false;
        }

        private void ConfirmCandidate(AutocompleteCandidate candidate)
        {
            Close();
            OnCandidateSelected?.Invoke(candidate);
        }

        private void FilterAndDisplay(string query, Vector2 position)
        {
            query = query?.Trim() ?? "";

            m_FilteredCandidates.Clear();
            if (string.IsNullOrEmpty(query))
            {
                m_FilteredCandidates.AddRange(m_AllCandidates.Take(15));
            }
            else
            {
                m_FilteredCandidates.AddRange(
                    m_AllCandidates
                        .Where(c => c.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    (c.Subtitle != null && c.Subtitle.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                        .OrderByDescending(c => c.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                        .Take(15)
                );
            }

            if (m_FilteredCandidates.Count == 0)
            {
                style.display = DisplayStyle.None;
                return;
            }

            m_SelectedIndex = 0;
            style.display = DisplayStyle.Flex;
            style.left = Mathf.Max(8, position.x);
            style.top = Mathf.Max(8, position.y);

            RebuildScrollView();
        }

        private void RebuildScrollView()
        {
            m_ScrollView.Clear();

            for (int i = 0; i < m_FilteredCandidates.Count; i++)
            {
                int index = i;
                var cand = m_FilteredCandidates[i];

                var itemRow = new VisualElement
                {
                    style = { backgroundColor = index == m_SelectedIndex ? NotesTheme.BgActive : Color.clear }
                }.WithClass("fn-popup-item");

                itemRow.Add(NotePalette.IconElement(cand.Icon));

                var textCol = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
                textCol.Add(new Label(cand.Title)
                {
                    style = { fontSize = 13, color = NotesTheme.TextNormal, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis, whiteSpace = WhiteSpace.NoWrap }
                });

                if (!string.IsNullOrEmpty(cand.Subtitle))
                    textCol.Add(new Label(cand.Subtitle)
                    {
                        style = { fontSize = 11, color = NotesTheme.TextFaint, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis, whiteSpace = WhiteSpace.NoWrap }
                    });
                itemRow.Add(textCol);

                if (!string.IsNullOrEmpty(cand.Category))
                    itemRow.Add(new Label(cand.Category) { style = { color = NotesTheme.TextMuted } }.WithClass("fn-kbd"));

                itemRow.RegisterCallback<PointerEnterEvent>(e =>
                {
                    m_SelectedIndex = index;
                    RefreshHighlight();
                });

                itemRow.RegisterCallback<ClickEvent>(e =>
                {
                    ConfirmCandidate(cand);
                });

                m_ScrollView.Add(itemRow);
            }
        }

        private void RefreshHighlight()
        {
            var children = m_ScrollView.Children().ToList();
            for (int i = 0; i < children.Count; i++)
                children[i].style.backgroundColor = i == m_SelectedIndex ? NotesTheme.BgActive : Color.clear;

            if (m_SelectedIndex >= 0 && m_SelectedIndex < children.Count)
            {
                m_ScrollView.ScrollTo(children[m_SelectedIndex]);
            }
        }

        private void BuildWikiLinkCandidates()
        {
            m_AllCandidates.Clear();

            // 1. Existing Notes
            if (NoteStore.Instance?.Notes != null)
            {
                foreach (var note in NoteStore.Instance.Notes)
                {
                    m_AllCandidates.Add(new AutocompleteCandidate(
                        note.Title,
                        note.RelativePath ?? (note.Title + ".md"),
                        "file-text",
                        $"[[{note.LinkName}]]",
                        "Note"
                    ));
                }
            }

            // 2. Project C# Scripts (Cached)
            EnsureProjectScriptsCache();
            if (s_CachedProjectScripts != null)
            {
                m_AllCandidates.AddRange(s_CachedProjectScripts);
            }
        }

        private void BuildTagCandidates()
        {
            m_AllCandidates.Clear();

            var tagCounts = NoteStore.Instance?.Index?.TagCounts;
            if (tagCounts != null)
            {
                foreach (var kvp in tagCounts.OrderByDescending(k => k.Value))
                {
                    m_AllCandidates.Add(new AutocompleteCandidate(
                        $"#{kvp.Key}",
                        $"{kvp.Value} notes",
                        "hash",
                        $"#{kvp.Key} ",
                        "Tag"
                    ));
                }
            }
        }

        private void BuildSlashCandidates()
        {
            m_AllCandidates.Clear();
            // "$|" marks where the caret goes after insertion.
            m_AllCandidates.Add(new AutocompleteCandidate("Heading 1", "Major section heading", "heading-1", "# ", "Block"));
            m_AllCandidates.Add(new AutocompleteCandidate("Heading 2", "Section heading", "heading-2", "## ", "Block"));
            m_AllCandidates.Add(new AutocompleteCandidate("Heading 3", "Sub-section heading", "heading-3", "### ", "Block"));
            m_AllCandidates.Add(new AutocompleteCandidate("To-Do Task", "Interactive task checkbox", "square-check", "- [ ] ", "Task"));
            m_AllCandidates.Add(new AutocompleteCandidate("C# Code Block", "Syntax highlighted snippet", "square-code", "```csharp\n$|\n```", "Code"));
            m_AllCandidates.Add(new AutocompleteCandidate("Run in Terminal", "Executable shell command", "terminal", "```sh\n$|\n```", "Shell"));
            m_AllCandidates.Add(new AutocompleteCandidate("Bullet List", "Unordered list bullet", "list", "- ", "List"));
            m_AllCandidates.Add(new AutocompleteCandidate("Numbered List", "Ordered numbered list", "list-ordered", "1. ", "List"));
            m_AllCandidates.Add(new AutocompleteCandidate("Quote", "Indented blockquote", "quote", "> ", "Block"));
            m_AllCandidates.Add(new AutocompleteCandidate("Callout: Note", "Highlighted note box", "pencil", "> [!note] $|", "Callout"));
            m_AllCandidates.Add(new AutocompleteCandidate("Callout: Tip", "Highlighted tip box", "lightbulb", "> [!tip] $|", "Callout"));
            m_AllCandidates.Add(new AutocompleteCandidate("Callout: Warning", "Highlighted warning box", "triangle-alert", "> [!warning] $|", "Callout"));
            m_AllCandidates.Add(new AutocompleteCandidate("Callout: Bug", "Highlighted bug box", "bug", "> [!bug] $|", "Callout"));
            m_AllCandidates.Add(new AutocompleteCandidate("Table", "Markdown 2-column table", "table", "| $|Header 1 | Header 2 |\n| --- | --- |\n| Item A | Item B |", "Table"));
            m_AllCandidates.Add(new AutocompleteCandidate("Image", "Embed an image from the vault or project", "image", "![[$|]]", "Embed"));
            m_AllCandidates.Add(new AutocompleteCandidate("Divider", "Horizontal ruler divider", "minus", "---\n", "Format"));
            m_AllCandidates.Add(new AutocompleteCandidate("Date", "Today's date", "calendar", DateTime.Now.ToString("yyyy-MM-dd"), "Inline"));
            m_AllCandidates.Add(new AutocompleteCandidate("Timestamp", "Current local date and time", "calendar-days", DateTime.Now.ToString("yyyy-MM-dd HH:mm"), "Inline"));
        }

        private static void EnsureProjectScriptsCache()
        {
            double now = EditorApplication.timeSinceStartup;
            if (s_CachedProjectScripts != null && now - s_LastScriptCacheTime < 30.0)
            {
                return;
            }

            s_LastScriptCacheTime = now;
            s_CachedProjectScripts = new List<AutocompleteCandidate>();

            try
            {
                if (Directory.Exists(Application.dataPath))
                {
                    var csFiles = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories);
                    foreach (var file in csFiles)
                    {
                        string fileName = Path.GetFileName(file);
                        // Skip temporary or hidden files
                        if (fileName.StartsWith(".") || fileName.StartsWith("~")) continue;

                        s_CachedProjectScripts.Add(new AutocompleteCandidate(
                            fileName,
                            "Assets" + file.Substring(Application.dataPath.Length).Replace('\\', '/'),
                            "code",
                            $"[[{fileName}:1]]",
                            "Script"
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FuzzyNotes] Failed to scan project scripts: {ex.Message}");
            }
        }
    }
}
