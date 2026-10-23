using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Ctrl+Alt+N scratchpad (Ctrl+Shift+N is Unity's "Create Empty"). Saves to a new note or appends to today's daily note, optionally with editor context
    /// (scene, selected object, last Console error) and — in Play Mode — a Game view screenshot.
    /// </summary>
    public class QuickCaptureWindow : EditorWindow
    {
        private const string TargetPref = "FuzzyNotes.QuickCapture.Target";
        private const string ContextPref = "FuzzyNotes.QuickCapture.Context";
        private const string ScreenshotPref = "FuzzyNotes.QuickCapture.Screenshot";
        private static readonly List<string> Targets = new List<string> { "New note", "Today's daily note" };

        private TextField m_TitleField;
        private TextField m_ContentField;
        private DropdownField m_TargetField;
        private Toggle m_ContextToggle;
        private Toggle m_ScreenshotToggle;
        private Label m_StatusLabel;

        [MenuItem("Window/Fuzzy Logic Labs/Fuzzy IDE/Quick Scratchpad %&n", false, 21)]
        public static void OpenQuickCapture()
        {
            var win = GetWindow<QuickCaptureWindow>(true, "Quick Scratchpad", true);
            win.minSize = new Vector2(440, 300);
            win.maxSize = new Vector2(640, 480);
            win.ShowUtility();
        }

        private void CreateGUI()
        {
            rootVisualElement.Clear();
            NotesTheme.Apply(rootVisualElement);
            rootVisualElement.style.paddingLeft = 12;
            rootVisualElement.style.paddingRight = 12;
            rootVisualElement.style.paddingTop = 10;
            rootVisualElement.style.paddingBottom = 10;

            bool isPlayMode = EditorApplication.isPlaying;
            string defaultTitle = isPlayMode ? $"Playtest Note ({DateTime.Now:HH:mm})" : $"Scratchpad ({DateTime.Now:HH:mm})";

            rootVisualElement.Add(new Label("Quick Scratchpad")
            {
                style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, color = NotesTheme.TextNormal, marginBottom = 8 }
            });

            // Where to save
            var targetRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
            m_TargetField = new DropdownField(Targets, Mathf.Clamp(EditorPrefs.GetInt(TargetPref, 0), 0, Targets.Count - 1)) { style = { width = 170, marginRight = 6 } };
            m_TargetField.RegisterValueChangedCallback(_ =>
            {
                EditorPrefs.SetInt(TargetPref, m_TargetField.index);
                UpdateTitleVisibility();
            });
            targetRow.Add(m_TargetField);

            m_TitleField = new TextField { value = defaultTitle, style = { flexGrow = 1, fontSize = 12 } };
            targetRow.Add(m_TitleField);
            rootVisualElement.Add(targetRow);

            m_ContentField = new TextField
            {
                multiline = true,
                value = "- [ ] ",
                style = { flexGrow = 1, fontSize = 12, marginBottom = 6 }
            };
            rootVisualElement.Add(m_ContentField);

            // What to attach
            var optionsRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
            m_ContextToggle = new Toggle("Attach context") { value = EditorPrefs.GetBool(ContextPref, isPlayMode), style = { marginRight = 12 } };
            m_ContextToggle.tooltip = "Adds the scene, play time, selected object and the last Console error (with a link to its code line).";
            m_ContextToggle.RegisterValueChangedCallback(e => EditorPrefs.SetBool(ContextPref, e.newValue));
            optionsRow.Add(m_ContextToggle);

            m_ScreenshotToggle = new Toggle("Game view screenshot") { value = isPlayMode && EditorPrefs.GetBool(ScreenshotPref, false) };
            m_ScreenshotToggle.tooltip = isPlayMode ? "Saves a screenshot into the vault's assets folder and embeds it." : "Available in Play Mode.";
            m_ScreenshotToggle.SetEnabled(isPlayMode);
            m_ScreenshotToggle.RegisterValueChangedCallback(e => EditorPrefs.SetBool(ScreenshotPref, e.newValue));
            optionsRow.Add(m_ScreenshotToggle);
            rootVisualElement.Add(optionsRow);

            if (CaptureContext.HasRecentError)
            {
                optionsRow.Add(new Label("Recent Console error") { tooltip = "A Console error will be included when 'Attach context' is on.", style = { fontSize = 10, color = NotesTheme.Warning, marginLeft = 8 } });
            }

            // Focus on content field immediately
            m_ContentField.RegisterCallback<AttachToPanelEvent>(e => m_ContentField.Focus());

            // Handle Ctrl+Enter to save
            m_ContentField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if ((evt.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0 && evt.keyCode == KeyCode.Return)
                {
                    SaveAndClose();
                    evt.StopPropagation();
                }
            });

            var bottomRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween, alignItems = Align.Center }
            };

            m_StatusLabel = new Label("Press Ctrl+Enter to save & close")
            {
                style = { fontSize = 10, color = NotesTheme.TextFaint }
            };
            bottomRow.Add(m_StatusLabel);

            var buttonGroup = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            buttonGroup.Add(new Button(Close) { text = "Cancel", style = { marginRight = 6 } });
            buttonGroup.Add(new Button(SaveAndClose)
            {
                text = "Save note",
                style = { backgroundColor = NotesTheme.Accent, color = NotesTheme.TextOnAccent }
            });
            bottomRow.Add(buttonGroup);
            rootVisualElement.Add(bottomRow);

            UpdateTitleVisibility();
        }

        private bool ToDailyNote => m_TargetField != null && m_TargetField.index == 1;

        private void UpdateTitleVisibility()
        {
            m_TitleField.style.display = ToDailyNote ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void SaveAndClose()
        {
            string content = (m_ContentField.value ?? "").TrimEnd();
            bool emptyTask = content == "- [ ]" || content.Length == 0;
            bool attachContext = m_ContextToggle.value;
            bool screenshot = m_ScreenshotToggle.value && EditorApplication.isPlaying;
            if (emptyTask && !attachContext && !screenshot)
            {
                Close();
                return;
            }

            var store = NoteStore.Instance;
            store.EnsureLoaded();

            var parts = new List<string>();
            if (attachContext) parts.Add(CaptureContext.BuildContextMarkdown(includeError: true).TrimEnd());
            if (!emptyTask) parts.Add(content);
            if (screenshot)
            {
                string embed = CaptureContext.CaptureGameView(store.PrimaryVault);
                if (embed != null) parts.Add(embed);
            }
            string body = string.Join("\n\n", parts) + "\n";

            if (ToDailyNote)
            {
                var daily = store.OpenDailyNote(DateTime.Today, NoteSettings.DailyOptions, NoteTemplateContextFactory.Create());
                store.AppendToNote(daily, $"\n### {DateTime.Now:HH:mm} {(EditorApplication.isPlaying ? "🎮 Playtest" : "⚡ Capture")}\n" + body);
            }
            else
            {
                var note = store.CreateNote(m_TitleField.value, body, new List<string> { EditorApplication.isPlaying ? "playtest" : "scratchpad" });
                store.SetActiveNote(note);
            }

            Close();
        }
    }
}
