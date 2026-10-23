using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Search pane of the left sidebar, like Obsidian's: operator search over all vaults with results grouped per
    /// note (every matching line, collapsible), match case, sort, copy results and an operator cheat sheet.
    /// </summary>
    public class SearchView : VisualElement
    {
        private const string MatchCasePrefKey = "FuzzyNotes.Search.MatchCase";
        private const string SortPrefKey = "FuzzyNotes.Search.Sort";
        private const int MaxNotes = 300;

        private static readonly (string syntax, string insert, string help)[] Operators =
        {
            ("word another", "", "notes containing all words"),
            ("\"exact phrase\"", "\"\"", "the words in this order"),
            ("-word", "-", "leave out notes with the word"),
            ("tag:name  #name", "tag:", "notes with a tag (and its subtags)"),
            ("path:folder", "path:", "notes under a folder"),
            ("file:name", "file:", "notes whose file name contains the text"),
            ("vault:id", "vault:", "notes of one vault (project, personal…)"),
            ("task:todo|done|doing|any", "task:", "notes with tasks in that state")
        };

        private readonly TextField m_Input;
        private readonly Button m_MatchCaseButton;
        private readonly VisualElement m_Help;
        private readonly Label m_Summary;
        private readonly ListView m_List;
        private readonly HashSet<string> m_Collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<NoteSearchResult> m_Results = new List<NoteSearchResult>();
        private List<SearchRow> m_Rows = new List<SearchRow>();
        private bool m_MatchCase;
        private SearchSort m_Sort;
        private IVisualElementScheduledItem m_PendingSearch;

        /// <summary>A result was chosen: (note, body line or -1, open mode).</summary>
        public event Action<NoteItem, int, NoteOpenMode> OnOpenResult;
        public event Action<NoteItem> OnRevealInExplorer;

        public SearchView()
        {
            style.flexGrow = 1;
            style.backgroundColor = NotesTheme.BgSecondary;
            m_MatchCase = EditorPrefs.GetBool(MatchCasePrefKey, false);
            m_Sort = (SearchSort)EditorPrefs.GetInt(SortPrefKey, (int)SearchSort.Relevance);

            var box = new VisualElement { style = { paddingLeft = 8, paddingRight = 8, paddingTop = 8, paddingBottom = 2 } };
            m_Input = new TextField().WithClass("fn-input");
            m_Input.textEdition.placeholder = "Search…";
            m_Input.RegisterValueChangedCallback(_ => ScheduleSearch(120));
            m_Input.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Escape && !string.IsNullOrEmpty(m_Input.value)) { m_Input.value = ""; e.StopPropagation(); }
                else if (e.keyCode == KeyCode.DownArrow && m_Rows.Count > 0)
                {
                    m_List.Focus();
                    m_List.SetSelection(0);
                    e.StopPropagation();
                }
                else if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && m_Rows.Count > 0)
                {
                    Open(m_Rows[0], e.altKey ? NoteOpenMode.SplitRight : NoteOpenMode.Default);
                    e.StopPropagation();
                }
            });
            box.Add(m_Input);
            Add(box);

            var tools = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 6, paddingRight = 6, flexShrink = 0 } };
            m_MatchCaseButton = new Button(ToggleMatchCase) { text = "Aa", tooltip = "Match case" }.WithClass("fn-icon-btn");
            m_MatchCaseButton.style.fontSize = 12;
            m_MatchCaseButton.style.color = NotesTheme.TextNormal;
            NotesIcons.SetToggled(m_MatchCaseButton, m_MatchCase);
            tools.Add(m_MatchCaseButton);
            tools.Add(NotesIcons.IconButton("arrow-up-down", "Sort results", ShowSortMenu));
            tools.Add(NotesIcons.IconButton("chevrons-down-up", "Collapse / expand all results", ToggleCollapseAll));
            tools.Add(NotesIcons.IconButton("copy", "Copy results as links", CopyResults));
            tools.Add(new VisualElement { style = { flexGrow = 1 } });
            tools.Add(NotesIcons.IconButton("info", "Search operators", () => m_Help.style.display = m_Help.style.display == DisplayStyle.Flex ? DisplayStyle.None : DisplayStyle.Flex));
            Add(tools);

            m_Help = BuildHelp();
            Add(m_Help);

            m_Summary = new Label { style = { color = NotesTheme.TextFaint } }.WithClass("fn-section-title");
            Add(m_Summary);

            m_List = new ListView
            {
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                selectionType = SelectionType.Single,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = m_Rows,
                style = { flexGrow = 1 }
            }.WithClass("fn-explorer");
            m_List.itemsChosen += items =>
            {
                if (items.FirstOrDefault() is SearchRow row) Open(row, NoteOpenMode.Default);
            };
            m_List.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.UpArrow && m_List.selectedIndex <= 0) { m_Input.Focus(); e.StopPropagation(); }
                else if ((e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow) && m_List.selectedItem is SearchRow row)
                {
                    SetCollapsed(row.Result, e.keyCode == KeyCode.LeftArrow);
                    e.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
            Add(m_List);

            UpdateSummary();

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged += HandleNotesChanged;
                NoteStore.OnNoteSaved += HandleNoteSaved;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged -= HandleNotesChanged;
                NoteStore.OnNoteSaved -= HandleNoteSaved;
            });
        }

        /// <summary>Focuses the search box, optionally replacing the query.</summary>
        public void FocusSearch(string query = null)
        {
            if (query != null) m_Input.value = query;
            schedule.Execute(() =>
            {
                m_Input.Focus();
                m_Input.SelectAll();
            });
        }

        private void HandleNotesChanged() => ScheduleSearch(300);
        private void HandleNoteSaved(NoteItem _) => ScheduleSearch(300);

        private void ScheduleSearch(long delay)
        {
            m_PendingSearch?.Pause();
            m_PendingSearch = schedule.Execute(RunSearch).StartingIn(delay);
        }

        private void RunSearch()
        {
            string text = m_Input.value ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                m_Results = new List<NoteSearchResult>();
            }
            else
            {
                var query = NoteSearchQuery.Parse(text);
                query.MatchCase = m_MatchCase;
                m_Results = SearchRows.Sort(NoteSearch.Run(NoteStore.Instance.Notes, query), m_Sort);
            }
            RebuildRows();
        }

        private void RebuildRows()
        {
            var shown = m_Results.Count > MaxNotes ? m_Results.GetRange(0, MaxNotes) : m_Results;
            m_Rows = SearchRows.Build(shown, r => m_Collapsed.Contains(r.Note.FilePath ?? r.Note.Title));
            m_List.itemsSource = m_Rows;
            m_List.RefreshItems();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (string.IsNullOrWhiteSpace(m_Input.value))
            {
                m_Summary.text = "Type to search all notes";
                return;
            }
            int notes = m_Results.Count;
            int matches = SearchRows.TotalMatches(m_Results);
            string text = notes == 0 ? "No results" : $"{notes} {(notes == 1 ? "note" : "notes")}" + (matches > 0 ? $" · {matches} {(matches == 1 ? "match" : "matches")}" : "");
            if (notes > MaxNotes) text += $" · showing the first {MaxNotes}";
            m_Summary.text = text;
        }

        // =====================================================================
        // Options
        // =====================================================================

        private void ToggleMatchCase()
        {
            m_MatchCase = !m_MatchCase;
            EditorPrefs.SetBool(MatchCasePrefKey, m_MatchCase);
            NotesIcons.SetToggled(m_MatchCaseButton, m_MatchCase);
            RunSearch();
        }

        private void ShowSortMenu()
        {
            var menu = new GenericMenu();
            void Add(string label, SearchSort sort) => menu.AddItem(new GUIContent(label), m_Sort == sort, () =>
            {
                m_Sort = sort;
                EditorPrefs.SetInt(SortPrefKey, (int)sort);
                RunSearch();
            });
            Add("Relevance", SearchSort.Relevance);
            Add("Modified time (new to old)", SearchSort.Modified);
            Add("File name (A to Z)", SearchSort.TitleAscending);
            Add("File name (Z to A)", SearchSort.TitleDescending);
            menu.ShowAsContext();
        }

        private void ToggleCollapseAll()
        {
            bool anyExpanded = m_Results.Any(r => !m_Collapsed.Contains(r.Note.FilePath ?? r.Note.Title));
            m_Collapsed.Clear();
            if (anyExpanded) foreach (var r in m_Results) m_Collapsed.Add(r.Note.FilePath ?? r.Note.Title);
            RebuildRows();
        }

        private void SetCollapsed(NoteSearchResult result, bool collapsed)
        {
            string key = result.Note.FilePath ?? result.Note.Title;
            if (collapsed ? !m_Collapsed.Add(key) : !m_Collapsed.Remove(key)) return;
            RebuildRows();
            int index = m_Rows.FindIndex(r => r.Kind == SearchRowKind.Note && r.Result == result);
            if (index >= 0) m_List.SetSelectionWithoutNotify(new[] { index });
        }

        private void CopyResults()
        {
            if (m_Results.Count == 0) { NoteToast.Show("Nothing to copy"); return; }
            EditorGUIUtility.systemCopyBuffer = SearchRows.ToMarkdown(m_Results);
            NoteToast.Show($"Copied {m_Results.Count} {(m_Results.Count == 1 ? "link" : "links")}", NoteToastKind.Success);
        }

        private VisualElement BuildHelp()
        {
            var help = new VisualElement
            {
                style =
                {
                    display = DisplayStyle.None, marginLeft = 8, marginRight = 8, marginTop = 4, paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    backgroundColor = NotesTheme.BgRaised, borderTopLeftRadius = 6, borderTopRightRadius = 6, borderBottomLeftRadius = 6, borderBottomRightRadius = 6
                }
            };
            help.Add(new Label("Search operators — click one to add it") { style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextMuted, marginBottom = 4 } });
            foreach (var (syntax, insert, text) in Operators)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, minHeight = 20, alignItems = Align.Center } }.WithClass("fn-row");
                row.style.marginLeft = 0; row.style.marginRight = 0;
                row.Add(new Label(syntax) { style = { width = 140, flexShrink = 0, fontSize = 11, color = NotesTheme.Accent } });
                row.Add(new Label(text) { style = { flexShrink = 1, fontSize = 11, color = NotesTheme.TextFaint, whiteSpace = WhiteSpace.Normal } });
                if (insert.Length > 0) row.RegisterCallback<ClickEvent>(_ => InsertOperator(insert));
                help.Add(row);
            }
            return help;
        }

        private void InsertOperator(string op)
        {
            string text = (m_Input.value ?? "").TrimEnd();
            m_Input.value = text.Length == 0 ? op : text + " " + op;
            schedule.Execute(() =>
            {
                m_Input.Focus();
                int caret = op == "\"\"" ? m_Input.value.Length - 1 : m_Input.value.Length;
                m_Input.SelectRange(caret, caret);
            });
        }

        // =====================================================================
        // Rows
        // =====================================================================

        private sealed class RowElements
        {
            public VisualElement Head;
            public NotesIcon Arrow;
            public Label Title;
            public Label Folder;
            public Label Count;
            public Label Snippet;
            public SearchRow Data;
        }

        private VisualElement MakeRow()
        {
            var e = new RowElements();
            var row = new VisualElement { style = { paddingTop = 2, paddingBottom = 2 }, userData = e };

            e.Head = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, height = 24 } };
            e.Arrow = NotesIcons.Create("chevron-down", 14f, NotesTheme.TextFaint);
            e.Arrow.pickingMode = PickingMode.Position;
            e.Arrow.style.marginLeft = 2;
            e.Arrow.style.marginRight = 4;
            e.Arrow.RegisterCallback<PointerDownEvent>(ev =>
            {
                if (ev.button != 0 || e.Data == null) return;
                SetCollapsed(e.Data.Result, !m_Collapsed.Contains(e.Data.Result.Note.FilePath ?? e.Data.Result.Note.Title));
                ev.StopPropagation();
            });
            e.Head.Add(e.Arrow);
            e.Title = new Label { style = { unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextNormal } }.WithClass("fn-row-label");
            e.Head.Add(e.Title);
            e.Folder = new Label { style = { color = NotesTheme.TextFaint } }.WithClass("fn-row-meta");
            e.Head.Add(e.Folder);
            e.Count = new Label
            {
                style =
                {
                    fontSize = 10, marginLeft = 6, paddingLeft = 5, paddingRight = 5, flexShrink = 0, color = NotesTheme.TextMuted, backgroundColor = NotesTheme.BgActive,
                    borderTopLeftRadius = 7, borderTopRightRadius = 7, borderBottomLeftRadius = 7, borderBottomRightRadius = 7
                }
            };
            e.Head.Add(e.Count);
            row.Add(e.Head);

            e.Snippet = new Label { enableRichText = true, style = { fontSize = 12, color = NotesTheme.TextMuted, whiteSpace = WhiteSpace.Normal, paddingLeft = 22, paddingRight = 6 } };
            row.Add(e.Snippet);

            row.RegisterCallback<ClickEvent>(ev =>
            {
                if (e.Data != null && ev.target != e.Arrow) Open(e.Data, ev.altKey ? NoteOpenMode.SplitRight : NoteOpenMode.Default);
            });
            row.RegisterCallback<ContextClickEvent>(ev =>
            {
                if (e.Data != null) ShowRowMenu(e.Data);
                ev.StopPropagation();
            });
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var e = (RowElements)element.userData;
            var row = m_Rows[index];
            e.Data = row;
            var result = row.Result;
            bool head = row.Kind == SearchRowKind.Note;

            e.Head.style.display = head ? DisplayStyle.Flex : DisplayStyle.None;
            e.Snippet.style.display = head ? DisplayStyle.None : DisplayStyle.Flex;
            element.style.paddingTop = head ? 4 : 1;

            if (head)
            {
                var note = result.Note;
                bool collapsed = m_Collapsed.Contains(note.FilePath ?? note.Title);
                int count = result.Matches.Count + result.MoreMatches;
                e.Arrow.SetIcon(collapsed ? "chevron-right" : "chevron-down");
                e.Arrow.style.visibility = count > 0 ? Visibility.Visible : Visibility.Hidden;
                e.Title.text = note.Title;
                e.Folder.text = note.IsExternal ? "external" : note.Folder.Length > 0 ? note.Folder : NoteStore.Instance.Vaults.Count > 1 ? NoteStore.Instance.VaultOf(note)?.Name ?? "" : "";
                e.Count.text = count.ToString();
                e.Count.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            else if (row.Kind == SearchRowKind.Match)
            {
                e.Snippet.text = HighlightSnippet(row.Match);
                e.Snippet.style.color = NotesTheme.TextMuted;
            }
            else
            {
                e.Snippet.text = $"{row.Count} more {(row.Count == 1 ? "match" : "matches")} in this note";
                e.Snippet.style.color = NotesTheme.TextFaint;
            }
        }

        private void Open(SearchRow row, NoteOpenMode mode)
        {
            int line = row.Kind == SearchRowKind.Match ? row.Match.Line : row.Result.Line;
            OnOpenResult?.Invoke(row.Result.Note, line, mode);
        }

        private void ShowRowMenu(SearchRow row)
        {
            var note = row.Result.Note;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Open"), false, () => Open(row, NoteOpenMode.Default));
            menu.AddItem(new GUIContent("Open to the Right"), false, () => Open(row, NoteOpenMode.SplitRight));
            menu.AddItem(new GUIContent("Open Below"), false, () => Open(row, NoteOpenMode.SplitDown));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Link"), false, () => EditorGUIUtility.systemCopyBuffer = $"[[{note.LinkName}]]");
            if (!note.IsExternal) menu.AddItem(new GUIContent("Reveal in Files"), false, () => OnRevealInExplorer?.Invoke(note));
            menu.AddItem(new GUIContent("Reveal in File Explorer"), false, () => { if (File.Exists(note.FilePath)) EditorUtility.RevealInFinder(note.FilePath); });
            menu.ShowAsContext();
        }

        private static string HighlightSnippet(NoteSearchMatch m)
        {
            string s = m.Snippet ?? "";
            int a = Mathf.Clamp(m.MatchStart, 0, s.Length);
            int len = Mathf.Clamp(m.MatchLength, 0, s.Length - a);
            if (len == 0) return "<noparse>" + s + "</noparse>";

            var sb = new StringBuilder();
            if (a > 0) sb.Append("<noparse>").Append(s, 0, a).Append("</noparse>");
            sb.Append("<mark=").Append(NotesTheme.Hex(NotesTheme.SearchMatchCurrent)).Append("><color=").Append(NotesTheme.Hex(NotesTheme.TextNormal))
              .Append("><noparse>").Append(s, a, len).Append("</noparse></color></mark>");
            if (a + len < s.Length) sb.Append("<noparse>").Append(s, a + len, s.Length - a - len).Append("</noparse>");
            return sb.ToString();
        }
    }
}
