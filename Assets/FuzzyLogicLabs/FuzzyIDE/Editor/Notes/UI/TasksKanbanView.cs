using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    public class TasksKanbanView : VisualElement
    {
        private ScrollView m_ToDoColumn;
        private ScrollView m_InProgressColumn;
        private ScrollView m_DoneColumn;

        private Label m_ToDoCountLabel;
        private Label m_InProgressCountLabel;
        private Label m_DoneCountLabel;

        // Live // TODO comments from scripts (optional fourth column)
        private const string ShowCodeTodosPref = "FuzzyNotes.Kanban.ShowCodeTodos";
        private VisualElement m_CodeColumn;
        private ScrollView m_CodeTodoColumn;
        private Label m_CodeTodoCountLabel;

        public event Action OnBackToNotesClicked;

        public TasksKanbanView()
        {
            style.flexGrow = 1;
            style.flexDirection = FlexDirection.Column;
            style.backgroundColor = NotesTheme.BgPrimary;

            BuildUI();

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged += Refresh;
                NoteStore.OnNoteSaved += OnNoteSaved;
                CodeTodoIndex.OnChanged += RefreshCodeTodos;
                RefreshCodeTodos();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                NoteStore.OnNotesChanged -= Refresh;
                NoteStore.OnNoteSaved -= OnNoteSaved;
                CodeTodoIndex.OnChanged -= RefreshCodeTodos;
            });
        }

        private void OnNoteSaved(NoteItem note) => Refresh();

        private void BuildUI()
        {
            Clear();

            // 1. Top Bar
            var topBar = new VisualElement
            {
                style = { height = 44, paddingLeft = 8, paddingRight = 12, backgroundColor = NotesTheme.BgPrimary, borderBottomColor = NotesTheme.BorderSubtle }
            }.WithClass("fn-toolbar");

            topBar.Add(NotesIcons.IconButton("arrow-left", "Back to notes", () => OnBackToNotesClicked?.Invoke()));
            topBar.Add(NotesIcons.Create("square-kanban", 18f, NotesTheme.TextMuted));
            topBar.Add(new Label("Tasks") { style = { marginLeft = 8, fontSize = 15, unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextNormal, flexGrow = 1 } });

            bool showCode = EditorPrefs.GetBool(ShowCodeTodosPref, true);
            Button codeToggle = null;
            codeToggle = NotesIcons.IconButton("code", "Show // TODO, FIXME, HACK and BUG comments from your scripts as a live column", () =>
            {
                bool on = m_CodeColumn.style.display == DisplayStyle.None;
                EditorPrefs.SetBool(ShowCodeTodosPref, on);
                m_CodeColumn.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
                NotesIcons.SetToggled(codeToggle, on);
                RefreshCodeTodos();
            });
            NotesIcons.SetToggled(codeToggle, showCode);
            codeToggle.style.marginRight = 8;
            topBar.Add(codeToggle);

            var addTaskBtn = new Button(AddNewTaskToActiveNote)
            {
                tooltip = "Add a task to the active note",
                style = { backgroundColor = NotesTheme.Accent }
            }.WithClass("fn-btn", "fn-btn--primary");
            addTaskBtn.Add(NotesIcons.Create("plus", 14f, NotesTheme.TextOnAccent));
            addTaskBtn.Add(new Label("Add task") { style = { marginLeft = 4, color = NotesTheme.TextOnAccent } });
            topBar.Add(addTaskBtn);

            Add(topBar);

            // 2. Kanban Columns Container
            var columnsContainer = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    flexDirection = FlexDirection.Row,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 12,
                    paddingBottom = 12
                }
            };

            columnsContainer.Add(CreateColumn("To do", NotesTheme.Accent, out m_ToDoColumn, out m_ToDoCountLabel));
            columnsContainer.Add(CreateColumn("In progress", NotesTheme.Warning, out m_InProgressColumn, out m_InProgressCountLabel));
            columnsContainer.Add(CreateColumn("Done", NotesTheme.Success, out m_DoneColumn, out m_DoneCountLabel));

            // Column 4: code comments (live, read-only)
            m_CodeColumn = CreateColumn("Code TODOs", NotesTheme.Purple, out m_CodeTodoColumn, out m_CodeTodoCountLabel);
            m_CodeColumn.style.display = showCode ? DisplayStyle.Flex : DisplayStyle.None;
            columnsContainer.Add(m_CodeColumn);

            Add(columnsContainer);

            Refresh();
        }

        private static VisualElement CreateColumn(string headerText, Color accent, out ScrollView scrollView, out Label countLabel)
        {
            var col = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    flexBasis = 0,
                    marginLeft = 5,
                    marginRight = 5,
                    backgroundColor = NotesTheme.BgSecondary,
                    borderTopLeftRadius = 8, borderTopRightRadius = 8, borderBottomLeftRadius = 8, borderBottomRightRadius = 8
                }
            };

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 12, paddingRight = 10, paddingTop = 10, paddingBottom = 6 } };
            header.Add(new VisualElement
            {
                style =
                {
                    width = 8, height = 8, marginRight = 8,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4, borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    backgroundColor = accent
                }
            });
            header.Add(new Label(headerText) { style = { flexGrow = 1, fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextNormal } });

            countLabel = new Label("0") { style = { fontSize = 12, color = NotesTheme.TextFaint } };
            header.Add(countLabel);
            col.Add(header);

            scrollView = new ScrollView { style = { flexGrow = 1, paddingLeft = 8, paddingRight = 8, paddingTop = 2, paddingBottom = 8 } };
            col.Add(scrollView);

            return col;
        }

        public void Refresh()
        {
            m_ToDoColumn.Clear();
            m_InProgressColumn.Clear();
            m_DoneColumn.Clear();

            var allTasks = NoteStore.Instance.Index.AllTasks;
            int todoCount = 0;
            int inProgressCount = 0;
            int doneCount = 0;

            foreach (var task in allTasks)
            {
                var card = CreateTaskCard(task);
                switch (task.Status)
                {
                    case TaskStatus.ToDo:
                        m_ToDoColumn.Add(card);
                        todoCount++;
                        break;
                    case TaskStatus.InProgress:
                        m_InProgressColumn.Add(card);
                        inProgressCount++;
                        break;
                    case TaskStatus.Done:
                        m_DoneColumn.Add(card);
                        doneCount++;
                        break;
                }
            }

            m_ToDoCountLabel.text = todoCount.ToString();
            m_InProgressCountLabel.text = inProgressCount.ToString();
            m_DoneCountLabel.text = doneCount.ToString();
        }

        private const int MaxCodeTodoCards = 200;

        private void RefreshCodeTodos()
        {
            if (m_CodeTodoColumn == null || m_CodeColumn.style.display == DisplayStyle.None) return;
            m_CodeTodoColumn.Clear();

            var todos = CodeTodoIndex.All;
            m_CodeTodoCountLabel.text = CodeTodoIndex.IsReady ? todos.Count.ToString() : "…";
            if (!CodeTodoIndex.IsReady)
            {
                m_CodeTodoColumn.Add(new Label("Scanning scripts…") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-empty"));
                return;
            }

            for (int i = 0; i < todos.Count && i < MaxCodeTodoCards; i++) m_CodeTodoColumn.Add(CreateCodeTodoCard(todos[i]));
            if (todos.Count > MaxCodeTodoCards)
                m_CodeTodoColumn.Add(new Label($"+{todos.Count - MaxCodeTodoCards} more — use Ctrl+P → \"Go to code TODO\"") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-empty"));
        }

        private static VisualElement Card()
        {
            return new VisualElement
            {
                style =
                {
                    backgroundColor = NotesTheme.BgPrimary,
                    paddingLeft = 10, paddingRight = 10, paddingTop = 8, paddingBottom = 8,
                    marginBottom = 6,
                    borderTopLeftRadius = 6, borderTopRightRadius = 6, borderBottomLeftRadius = 6, borderBottomRightRadius = 6,
                    borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopColor = NotesTheme.BorderSubtle, borderBottomColor = NotesTheme.BorderSubtle,
                    borderLeftColor = NotesTheme.BorderSubtle, borderRightColor = NotesTheme.BorderSubtle
                }
            };
        }

        /// <summary>Small clickable chip: icon + text on a tinted background.</summary>
        private static Button Badge(string icon, string text, Color tint, Action onClick)
        {
            var badge = new Button(onClick)
            {
                tooltip = text,
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center, flexShrink = 1,
                    height = 20, maxWidth = Length.Percent(100), paddingLeft = 6, paddingRight = 7, marginRight = 4, marginBottom = 3,
                    backgroundColor = NotesTheme.WithAlpha(tint, 0.14f)
                }
            }.WithClass("fn-pill");
            badge.Add(NotesIcons.Create(icon, 12f, tint));
            badge.Add(new Label(text) { style = { marginLeft = 4, fontSize = 11, color = tint, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis, whiteSpace = WhiteSpace.NoWrap, flexShrink = 1 } });
            return badge;
        }

        private VisualElement CreateCodeTodoCard(CodeTodo todo)
        {
            var card = Card();
            card.tooltip = "Click to open in the IDE";
            card.RegisterCallback<ClickEvent>(_ => NoteLinkResolver.OpenCodeFile(todo.AssetPath, todo.Line + 1));

            var kindColor = todo.Kind == "TODO" ? NotesTheme.Accent : NotesTheme.Warning;
            card.Add(new Label($"<b><color={NotesTheme.Hex(kindColor)}>{todo.Kind}</color></b> <noparse>{todo.Text}</noparse>")
            {
                enableRichText = true,
                style = { fontSize = 13, color = NotesTheme.TextNormal, whiteSpace = WhiteSpace.Normal, marginBottom = 6 }
            });

            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            footer.Add(NotesIcons.Create("code", 12f, NotesTheme.TextFaint));
            footer.Add(new Label($"{todo.FileName}:{todo.Line + 1}") { style = { flexGrow = 1, marginLeft = 4, fontSize = 11, color = NotesTheme.TextFaint } });

            var track = new Button(() => TrackCodeTodo(todo))
            {
                text = "Track in note",
                tooltip = "Copy into a note as a task linked to this line",
                style = { height = 20, fontSize = 11, color = NotesTheme.TextNormal }
            }.WithClass("fn-btn");
            track.RegisterCallback<ClickEvent>(e => e.StopPropagation());
            footer.Add(track);
            card.Add(footer);
            return card;
        }

        private static void TrackCodeTodo(CodeTodo todo)
        {
            string text = string.IsNullOrWhiteSpace(todo.Text) ? todo.Kind : todo.Text;
            string markdown = $"\n- [ ] {text} [[{todo.Link}]]\n";
            NoteTargetPicker.Pick("Track this TODO in note…", null, note => CodeNotes.AppendAndReveal(note, markdown, "[[" + todo.Link + "]]"));
        }

        private VisualElement CreateTaskCard(TaskItem task)
        {
            var card = Card();

            card.Add(new Label(task.Text)
            {
                style =
                {
                    fontSize = 13,
                    color = task.Status == TaskStatus.Done ? NotesTheme.TextMuted : NotesTheme.TextNormal,
                    whiteSpace = WhiteSpace.Normal,
                    marginBottom = 6
                }
            });

            // Badges: origin note, code link, asset link
            var badgesRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center } };
            badgesRow.Add(Badge("file-text", task.NoteTitle, NotesTheme.TextMuted, () => JumpToNote(task.NoteFilePath)));
            if (!string.IsNullOrEmpty(task.CodeLink))
                badgesRow.Add(Badge("code", task.CodeLink, NotesTheme.Accent, () => NoteLinkResolver.ResolveAndOpen(task.CodeLink)));
            if (!string.IsNullOrEmpty(task.AssetLink))
                badgesRow.Add(Badge("box", "Asset", NotesTheme.Purple, () => NoteLinkResolver.OpenAsset(task.AssetLink)));
            card.Add(badgesRow);

            // State transitions
            var actionRow = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    justifyContent = Justify.FlexEnd,
                    alignItems = Align.Center,
                    marginTop = 4,
                    paddingTop = 4,
                    borderTopWidth = 1,
                    borderTopColor = NotesTheme.BorderSubtle
                }
            };

            if (task.Status != TaskStatus.ToDo)
                actionRow.Add(CreateTransitionButton("circle", "To do", () => ChangeTaskStatus(task, TaskStatus.ToDo)));
            if (task.Status != TaskStatus.InProgress)
                actionRow.Add(CreateTransitionButton("circle-dot", "In progress", () => ChangeTaskStatus(task, TaskStatus.InProgress)));
            if (task.Status != TaskStatus.Done)
                actionRow.Add(CreateTransitionButton("check", "Done", () => ChangeTaskStatus(task, TaskStatus.Done)));

            card.Add(actionRow);
            return card;
        }

        private static Button CreateTransitionButton(string icon, string label, Action onClick)
        {
            var button = NotesIcons.IconButton(icon, "Move to " + label.ToLowerInvariant(), onClick, 13f);
            button.AddToClassList("fn-icon-btn--small");
            button.Add(new Label(label) { style = { marginLeft = 4, fontSize = 11, color = NotesTheme.TextNormal } });
            return button;
        }

        private void ChangeTaskStatus(TaskItem task, TaskStatus newStatus)
        {
            var note = NoteStore.Instance.Notes.FirstOrDefault(n => string.Equals(n.FilePath, task.NoteFilePath, StringComparison.OrdinalIgnoreCase));
            NoteStore.Instance.SetTaskStatus(note, task.LineIndex, newStatus);
        }

        private void JumpToNote(string filePath)
        {
            var note = NoteStore.Instance.Notes.FirstOrDefault(n => string.Equals(n.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (note != null)
            {
                NoteStore.Instance.SetActiveNote(note);
                OnBackToNotesClicked?.Invoke();
            }
        }

        /// <summary>One-line input provided by the workspace (the palette's prompt mode); a modal dialog without it.</summary>
        public Action<string, Action<string>> Prompt;

        private void AddNewTaskToActiveNote()
        {
            const string placeholder = "New task — you can include [[Script.cs:1]] or [[asset:...]]";
            if (Prompt != null) Prompt(placeholder, AddTask);
            else AddTask(EditorInputDialog.Show("Add Task", placeholder));
        }

        private void AddTask(string taskText)
        {
            if (string.IsNullOrWhiteSpace(taskText)) return;

            var targetNote = NoteStore.Instance.ActiveNote;
            if (targetNote == null)
            {
                targetNote = NoteStore.Instance.Notes.FirstOrDefault();
            }
            if (targetNote == null)
            {
                targetNote = NoteStore.Instance.CreateNote("Tasks Inbox");
            }

            NoteStore.Instance.AppendToNote(targetNote, $"\n- [ ] {taskText.Trim()}");
            Refresh();
        }
    }
}
