using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// Fuzzy Notes settings, laid out like Obsidian's: a list of sections on the left (General, Appearance, Editor,
    /// Hotkeys) and the selected section on the right. Appearance, editor and hotkey changes apply immediately;
    /// vault changes are applied with a button because they reload every note.
    /// </summary>
    public class NoteSettingsWindow : EditorWindow
    {
        private const string SectionPrefKey = "FuzzyNotes.Settings.Section";
        private static readonly string[] Sections = { "General", "Appearance", "Editor", "Hotkeys" };

        private VisualElement m_Nav;
        private ScrollView m_Content;
        private string m_Section;

        // General (applied with the button)
        private Toggle m_PersonalEnabled;
        private TextField m_PersonalPath;
        private VisualElement m_ExternalList;
        private readonly List<string> m_External = new List<string>();
        private Toggle m_ProjectMarkdown;
        private Toggle m_ProjectMarkdownInGraph;
        private TextField m_ProjectExcludes;
        private TextField m_DailyFolder;
        private TextField m_DailyFormat;
        private TextField m_DailyTemplate;
        private TextField m_TemplatesFolder;

        // Hotkeys
        private string m_HotkeyFilter = "";
        private IDEAction? m_Capturing;
        private IVisualElementScheduledItem m_PendingAccent;

        public static void Open()
        {
            var w = GetWindow<NoteSettingsWindow>(true, "Fuzzy Notes Settings");
            w.minSize = new Vector2(640, 460);
            w.Show();
        }

        private void OnEnable() => NoteSettings.ThemeChanged += Rebuild;
        private void OnDisable() => NoteSettings.ThemeChanged -= Rebuild;

        private void Rebuild()
        {
            if (rootVisualElement != null) CreateGUI();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.UnregisterCallback<KeyDownEvent>(OnCaptureKey, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnCaptureKey, TrickleDown.TrickleDown);
            NotesTheme.Apply(root);
            root.style.flexDirection = FlexDirection.Row;

            m_Nav = new VisualElement
            {
                style =
                {
                    width = 160, flexShrink = 0, paddingTop = 10, backgroundColor = NotesTheme.BgSecondary,
                    borderRightWidth = 1, borderRightColor = NotesTheme.BorderSubtle
                }
            };
            m_Nav.Add(new Label("Options") { style = { color = NotesTheme.TextFaint } }.WithClass("fn-section-title"));
            root.Add(m_Nav);

            m_Content = new ScrollView { style = { flexGrow = 1, paddingLeft = 18, paddingRight = 18, paddingTop = 12, paddingBottom = 12 } };
            root.Add(m_Content);

            m_Section = EditorPrefs.GetString(SectionPrefKey, Sections[0]);
            if (!Sections.Contains(m_Section)) m_Section = Sections[0];
            foreach (var section in Sections)
            {
                var item = new Label(section) { style = { paddingLeft = 10, fontSize = 13 } }.WithClass("fn-row");
                item.RegisterCallback<ClickEvent>(_ => ShowSection(section));
                m_Nav.Add(item);
            }
            ShowSection(m_Section);
        }

        private void ShowSection(string section)
        {
            m_Section = section;
            m_Capturing = null;
            EditorPrefs.SetString(SectionPrefKey, section);
            for (int i = 1; i < m_Nav.childCount; i++)
            {
                var item = (Label)m_Nav[i];
                bool active = item.text == section;
                item.style.backgroundColor = active ? NotesTheme.BgActive : Color.clear;
                item.style.color = active ? NotesTheme.TextNormal : NotesTheme.TextMuted;
            }

            m_Content.Clear();
            m_Content.Add(new Label(section) { style = { fontSize = 18, unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 8, color = NotesTheme.TextNormal } });
            switch (section)
            {
                case "Appearance": BuildAppearance(); break;
                case "Editor": BuildEditor(); break;
                case "Hotkeys": BuildHotkeys(); break;
                default: BuildGeneral(); break;
            }
        }

        // =====================================================================
        // General: vaults, daily notes, templates
        // =====================================================================

        private void BuildGeneral()
        {
            var root = m_Content;
            root.Add(Header("Vaults"));
            root.Add(Hint("The project vault (.fuzzynotes) is always loaded and shared through git."));

            m_PersonalEnabled = new Toggle("Personal vault") { value = NoteSettings.PersonalVaultEnabled, tooltip = "Notes that follow you across projects (not committed)" };
            root.Add(m_PersonalEnabled);
            var personalRow = Row();
            m_PersonalPath = new TextField { value = NoteSettings.PersonalVaultPath, style = { flexGrow = 1 } }.WithClass("fn-input");
            personalRow.Add(m_PersonalPath);
            personalRow.Add(new Button(() =>
            {
                string p = EditorUtility.OpenFolderPanel("Personal vault folder", m_PersonalPath.value, "");
                if (!string.IsNullOrEmpty(p)) m_PersonalPath.value = p;
            }) { text = "…" }.WithClass("fn-btn"));
            root.Add(personalRow);

            root.Add(new Label("Attached vaults (e.g. an Obsidian vault)") { style = { marginTop = 8, unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11 } });
            m_External.Clear();
            m_External.AddRange(NoteSettings.ExternalVaults);
            m_ExternalList = new VisualElement();
            root.Add(m_ExternalList);
            root.Add(new Button(() =>
            {
                string p = EditorUtility.OpenFolderPanel("Attach vault folder", "", "");
                if (!string.IsNullOrEmpty(p) && !m_External.Contains(p)) { m_External.Add(p); RebuildExternal(); }
            }) { text = "Attach Folder…", style = { alignSelf = Align.FlexStart, marginTop = 2 } }.WithClass("fn-btn"));
            RebuildExternal();

            root.Add(Header("Markdown files in Assets"));
            root.Add(Hint("README and documentation files of the Unity project, listed as an \"Assets\" vault. They stay where they are: new notes are always created in the project vault."));
            m_ProjectMarkdown = new Toggle("Show .md files from Assets") { value = NoteSettings.ShowProjectMarkdown };
            root.Add(m_ProjectMarkdown);
            m_ProjectMarkdownInGraph = new Toggle("Show them in the graph view") { value = NoteSettings.ShowProjectMarkdownInGraph, tooltip = "Off: only Assets files that a note links to appear in the graph" };
            root.Add(m_ProjectMarkdownInGraph);
            root.Add(new Label("Excluded folders and files (one per line)") { style = { marginTop = 8, unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11 } });
            m_ProjectExcludes = new TextField { multiline = true, value = string.Join("\n", NoteSettings.ProjectMarkdownExcludes), style = { minHeight = 90, whiteSpace = WhiteSpace.Normal } }.WithClass("fn-input");
            root.Add(m_ProjectExcludes);
            var excludeRow = Row();
            var excludeHint = Hint("A name (Plugins, LICENSE*) hides every folder or file with that name; a path (Assets/Vendor/Docs) hides that folder. * and ? are wildcards.");
            excludeHint.style.flexGrow = 1;
            excludeHint.style.flexShrink = 1;
            excludeRow.Add(excludeHint);
            excludeRow.Add(new Button(() => m_ProjectExcludes.value = string.Join("\n", NoteSettings.DefaultProjectMarkdownExcludes)) { text = "Reset" }.WithClass("fn-btn"));
            root.Add(excludeRow);

            root.Add(Header("Daily notes & templates"));
            m_DailyFolder = Field(root, "Daily notes folder", NoteSettings.DailyFolder);
            m_DailyFormat = Field(root, "File name date format", NoteSettings.DailyDateFormat);
            m_DailyTemplate = Field(root, "Daily template (note name)", NoteSettings.DailyTemplate);
            m_TemplatesFolder = Field(root, "Templates folder", NoteSettings.TemplatesFolder);
            root.Add(Hint("Template variables: {{date}}, {{date:dddd d MMMM}}, {{time}}, {{title}}, {{branch}}, {{scene}}, {{selection}}, {{project}}"));

            var buttons = Row();
            buttons.style.justifyContent = Justify.FlexEnd;
            buttons.style.marginTop = 14;
            buttons.Add(new Button(ApplyGeneral) { text = "Apply and reload vaults" }.WithClass("fn-btn"));
            root.Add(buttons);
        }

        private void RebuildExternal()
        {
            m_ExternalList.Clear();
            if (m_External.Count == 0)
            {
                m_ExternalList.Add(new Label("None") { style = { fontSize = 11, color = NotesTheme.TextFaint, unityFontStyleAndWeight = FontStyle.Italic } });
                return;
            }
            foreach (var path in m_External)
            {
                string p = path;
                var row = Row();
                row.Add(new Label(p + (Directory.Exists(Path.Combine(p, ".obsidian")) ? "   (Obsidian)" : "")) { style = { flexGrow = 1, fontSize = 11 } });
                row.Add(new Button(() => { m_External.Remove(p); RebuildExternal(); }) { text = "Remove" }.WithClass("fn-btn"));
                m_ExternalList.Add(row);
            }
        }

        private void ApplyGeneral()
        {
            NoteSettings.PersonalVaultEnabled = m_PersonalEnabled.value;
            NoteSettings.PersonalVaultPath = m_PersonalPath.value;
            NoteSettings.ExternalVaults = new List<string>(m_External);
            NoteSettings.ShowProjectMarkdown = m_ProjectMarkdown.value;
            NoteSettings.ShowProjectMarkdownInGraph = m_ProjectMarkdownInGraph.value;
            NoteSettings.ProjectMarkdownExcludes = m_ProjectExcludes.value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            NoteSettings.DailyFolder = m_DailyFolder.value.Trim();
            NoteSettings.DailyDateFormat = m_DailyFormat.value.Trim();
            NoteSettings.DailyTemplate = m_DailyTemplate.value.Trim();
            NoteSettings.TemplatesFolder = m_TemplatesFolder.value.Trim();

            NoteStore.Instance.ReloadVaultConfiguration();
            NoteToast.Show("Vault settings applied", NoteToastKind.Success);
        }

        // =====================================================================
        // Appearance
        // =====================================================================

        private void BuildAppearance()
        {
            var root = m_Content;
            root.Add(Header("Text"));
            var zoom = new SliderInt("Font size", 60, 200) { value = Mathf.RoundToInt(NoteSettings.EditorZoom * 100), tooltip = "Editor zoom (also Ctrl+wheel, Ctrl+= / Ctrl+-)" };
            zoom.labelElement.style.minWidth = 190;
            var zoomValue = new Label($"{zoom.value}%") { style = { minWidth = 44, unityTextAlign = TextAnchor.MiddleRight, color = NotesTheme.TextMuted } };
            zoom.Add(zoomValue);
            zoom.RegisterValueChangedCallback(e =>
            {
                zoomValue.text = $"{e.newValue}%";
                NoteSettings.EditorZoom = e.newValue / 100f;
            });
            root.Add(zoom);
            Setting(root, "Readable line length", "Limit the text to a comfortable column instead of the full pane width.", NoteSettings.ReadableLineLength, v => NoteSettings.ReadableLineLength = v);
            Setting(root, "Show inline title", "Show the note title as a heading at the top of the page (rename it there).", NoteSettings.ShowInlineTitle, v => NoteSettings.ShowInlineTitle = v);
            Setting(root, "Show formatting toolbar", "A toolbar above the editor; formatting is also in the right-click menu and / commands.", NoteSettings.ShowFormattingBar, v => NoteSettings.ShowFormattingBar = v);

            root.Add(Header("Interface"));
            Setting(root, "Show ribbon", "The vertical icon strip on the left of the window.", NoteSettings.ShowRibbon, v => NoteSettings.ShowRibbon = v);

            bool custom = NoteSettings.AccentColor.Length > 0;
            var accentRow = Row();
            var useCustom = new Toggle("Custom accent color") { value = custom, tooltip = "Otherwise the accent follows the IDE theme" };
            useCustom.labelElement.style.minWidth = 190;
            var color = new ColorField { value = NotesTheme.Accent, showAlpha = false, style = { width = 120, marginLeft = 8 } };
            color.SetEnabled(custom);
            useCustom.RegisterValueChangedCallback(e =>
            {
                color.SetEnabled(e.newValue);
                NoteSettings.AccentColor = e.newValue ? "#" + ColorUtility.ToHtmlStringRGB(color.value) : "";
            });
            color.RegisterValueChangedCallback(e =>
            {
                // the notes window rebuilds on every accent change: wait until the picker rests
                m_PendingAccent?.Pause();
                m_PendingAccent = rootVisualElement.schedule.Execute(() => NoteSettings.AccentColor = "#" + ColorUtility.ToHtmlStringRGB(e.newValue)).StartingIn(350);
            });
            accentRow.Add(useCustom);
            accentRow.Add(color);
            root.Add(accentRow);
            root.Add(Hint("Colors of the editor text, headings and code follow the IDE theme (Preferences ▸ Fuzzy Logic Labs ▸ Code IDE)."));
        }

        // =====================================================================
        // Editor
        // =====================================================================

        private void BuildEditor()
        {
            var root = m_Content;
            root.Add(Header("Behavior"));
            var view = new DropdownField("Default view for new panes", new List<string> { "Editing view", "Reading view" }, NoteSettings.DefaultReadingMode ? 1 : 0)
            {
                tooltip = "Ctrl+E switches between the two in a pane"
            };
            view.labelElement.style.minWidth = 190;
            view.RegisterValueChangedCallback(e => NoteSettings.DefaultReadingMode = e.newValue == "Reading view");
            root.Add(view);

            root.Add(Header("Files"));
            Setting(root, "Confirm note deletion", "Ask before moving notes to the trash. Otherwise a notice with Undo appears.", NoteSettings.ConfirmDelete, v => NoteSettings.ConfirmDelete = v);
        }

        // =====================================================================
        // Hotkeys
        // =====================================================================

        /// <summary>The IDE actions that belong to Fuzzy Notes, in a stable order.</summary>
        private static IEnumerable<IDEAction> HotkeyActions() =>
            Enum.GetValues(typeof(IDEAction)).Cast<IDEAction>()
                .Where(a => a.ToString().StartsWith("Note", StringComparison.Ordinal) || a == IDEAction.OpenNotes || a == IDEAction.QuickScratchpad);

        /// <summary>"NoteToggleLeftSidebar" → "Toggle left sidebar".</summary>
        public static string ActionTitle(IDEAction action)
        {
            switch (action)
            {
                case IDEAction.OpenNotes: return "Open Fuzzy Notes (global)";
                case IDEAction.QuickScratchpad: return "Quick capture (global)";
            }
            string name = action.ToString();
            if (name.StartsWith("Note", StringComparison.Ordinal)) name = name.Substring(4);
            string words = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
            return char.ToUpperInvariant(words[0]) + words.Substring(1);
        }

        private void BuildHotkeys()
        {
            var root = m_Content;
            root.Add(Hint("Click a shortcut and press the new keys. Esc cancels. Shortcuts are shared with the Code IDE keybindings."));
            var search = new TextField { value = m_HotkeyFilter, style = { marginTop = 6, marginBottom = 8 } }.WithClass("fn-input");
            search.textEdition.placeholder = "Filter…";
            var list = new VisualElement();
            search.RegisterValueChangedCallback(e => { m_HotkeyFilter = e.newValue; RebuildHotkeys(list); });
            root.Add(search);
            root.Add(list);
            RebuildHotkeys(list);
        }

        private void RebuildHotkeys(VisualElement list)
        {
            list.Clear();
            var keybindings = CodeIDESettings.Current.Keybindings;
            keybindings.InitializeLookup();
            var defaults = KeybindingSettings.GetDefaultBindingsList();

            foreach (var action in HotkeyActions())
            {
                string title = ActionTitle(action);
                if (m_HotkeyFilter.Length > 0 && title.IndexOf(m_HotkeyFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var entry = keybindings.Bindings.Find(b => b.Action == action);
                var binding = entry?.Binding;
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 30, borderBottomWidth = 1, borderBottomColor = NotesTheme.BorderSubtle } };

                var text = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
                text.Add(new Label(title) { style = { fontSize = 13, color = NotesTheme.TextNormal } });
                var conflicts = binding == null ? new List<IDEAction>() :
                    keybindings.Bindings.Where(b => b.Action != action && b.Binding != null && b.Binding.Key == binding.Key && b.Binding.Modifiers == binding.Modifiers).Select(b => b.Action).ToList();
                if (conflicts.Count > 0)
                    text.Add(new Label("Also used by " + string.Join(", ", conflicts.Select(ActionTitle))) { style = { fontSize = 11, color = NotesTheme.Warning } });
                row.Add(text);

                bool capturing = m_Capturing == action;
                var key = new Button(() => { m_Capturing = action; RebuildHotkeys(list); })
                {
                    text = capturing ? "Press keys…" : binding != null ? WorkspaceView.ShortcutText(action) : "Blank",
                    tooltip = "Change shortcut",
                    style = { minWidth = 120, color = capturing ? NotesTheme.Accent : NotesTheme.TextNormal }
                }.WithClass("fn-btn");
                row.Add(key);
                if (capturing) key.schedule.Execute(key.Focus);

                var fallback = defaults.Find(d => d.Action == action)?.Binding;
                bool isDefault = fallback == null || binding != null && binding.Key == fallback.Key && binding.Modifiers == fallback.Modifiers;
                var reset = NotesIcons.IconButton("rotate-ccw", "Restore default", () =>
                {
                    if (entry != null && fallback != null) entry.Binding = new KeyBinding(fallback.Key, fallback.Modifiers);
                    CodeIDESettings.Save();
                    RebuildHotkeys(list);
                });
                reset.style.visibility = isDefault ? Visibility.Hidden : Visibility.Visible;
                row.Add(reset);
                list.Add(row);
            }
            m_HotkeyList = list;
        }

        private VisualElement m_HotkeyList;

        private void OnCaptureKey(KeyDownEvent evt)
        {
            if (m_Capturing == null) return;
            evt.StopPropagation();
            if (evt.keyCode == KeyCode.None || IsModifierKey(evt.keyCode)) return; // wait for the real key

            var action = m_Capturing.Value;
            m_Capturing = null;
            if (evt.keyCode != KeyCode.Escape)
            {
                var mods = evt.modifiers & (EventModifiers.Control | EventModifiers.Command | EventModifiers.Alt | EventModifiers.Shift);
                if ((mods & EventModifiers.Command) != 0) mods = (mods & ~EventModifiers.Command) | EventModifiers.Control;
                var keybindings = CodeIDESettings.Current.Keybindings;
                var entry = keybindings.Bindings.Find(b => b.Action == action);
                if (entry == null) keybindings.Bindings.Add(entry = new ActionKeyBinding { Action = action });
                entry.Binding = new KeyBinding(evt.keyCode, mods);
                CodeIDESettings.Save();
            }
            if (m_HotkeyList != null) RebuildHotkeys(m_HotkeyList);
        }

        private static bool IsModifierKey(KeyCode key) =>
            key == KeyCode.LeftShift || key == KeyCode.RightShift || key == KeyCode.LeftControl || key == KeyCode.RightControl ||
            key == KeyCode.LeftAlt || key == KeyCode.RightAlt || key == KeyCode.LeftCommand || key == KeyCode.RightCommand ||
            key == KeyCode.AltGr || key == KeyCode.LeftWindows || key == KeyCode.RightWindows;

        // =====================================================================
        // Building blocks
        // =====================================================================

        /// <summary>Obsidian-style setting row: name and description on the left, the toggle on the right.</summary>
        private static void Setting(VisualElement parent, string name, string description, bool value, Action<bool> set)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingTop = 6, paddingBottom = 6, borderBottomWidth = 1, borderBottomColor = NotesTheme.BorderSubtle } };
            var text = new VisualElement { style = { flexGrow = 1, flexShrink = 1, marginRight = 12 } };
            text.Add(new Label(name) { style = { fontSize = 13, color = NotesTheme.TextNormal } });
            text.Add(new Label(description) { style = { fontSize = 11, color = NotesTheme.TextFaint, whiteSpace = WhiteSpace.Normal } });
            row.Add(text);
            var toggle = new Toggle { value = value };
            toggle.RegisterValueChangedCallback(e => set(e.newValue));
            row.Add(toggle);
            parent.Add(row);
        }

        private static Label Header(string text) => new Label(text) { style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, marginTop = 12, marginBottom = 4, color = NotesTheme.TextNormal } };

        private static Label Hint(string text) => new Label(text) { style = { whiteSpace = WhiteSpace.Normal, fontSize = 11, color = NotesTheme.TextFaint, marginBottom = 4 } };

        private static VisualElement Row() => new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2 } };

        private static TextField Field(VisualElement parent, string label, string value)
        {
            var f = new TextField(label) { value = value };
            f.labelElement.style.minWidth = 190;
            parent.Add(f);
            return f;
        }
    }
}
