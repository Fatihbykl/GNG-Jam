using System;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>Collapsible, type-aware editor for a note's front-matter properties.</summary>
    public class NotePropertiesPanel : VisualElement
    {
        private const string ExpandedPrefKey = "FuzzyNotes.PropertiesExpanded";

        private readonly Foldout m_Foldout;
        private NoteItem m_Note;

        /// <summary>Raised after a property changed (the owner schedules the save).</summary>
        public event Action<NoteItem> OnChanged;

        public NotePropertiesPanel()
        {
            style.flexShrink = 0;
            style.backgroundColor = NotesTheme.BgPrimary;
            style.borderBottomWidth = 1;
            style.borderBottomColor = NotesTheme.BorderSubtle;
            style.paddingLeft = 10; style.paddingRight = 10; style.paddingTop = 2; style.paddingBottom = 2;

            m_Foldout = new Foldout { text = "Properties", value = UnityEditor.EditorPrefs.GetBool(ExpandedPrefKey, false) };
            m_Foldout.style.fontSize = 12;
            m_Foldout.style.color = NotesTheme.TextMuted;
            m_Foldout.RegisterValueChangedCallback(e =>
            {
                if (e.target != m_Foldout) return;
                UnityEditor.EditorPrefs.SetBool(ExpandedPrefKey, e.newValue);
            });
            Add(m_Foldout);
        }

        public void Bind(NoteItem note)
        {
            m_Note = note;
            Rebuild();
        }

        public void Rebuild()
        {
            m_Foldout.Clear();
            style.display = m_Note == null || m_Note.IsExternal && m_Note.PropertyKeys.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (m_Note == null) return;

            var keys = m_Note.PropertyKeys;
            m_Foldout.text = keys.Count == 0 ? "Properties" : $"Properties ({keys.Count})";
            foreach (var key in keys) m_Foldout.Add(BuildRow(key));
            m_Foldout.Add(BuildAddRow());
        }

        private VisualElement BuildRow(string key)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, minHeight = 28, marginBottom = 1 } };
            bool readOnly = NoteItem.IsReadOnlyProperty(key);
            bool isList = m_Note.IsListProperty(key) || IsListKey(key);
            string value = m_Note.GetPropertyText(key);

            bool isBool = !isList && (value == "true" || value == "false");
            var keyBox = new VisualElement { tooltip = isList ? "List (comma separated)" : isBool ? "Checkbox" : "Text", style = { width = 140, flexShrink = 0, flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            keyBox.Add(NotesIcons.Create(isList ? "list" : isBool ? "square-check" : "text", 14f, NotesTheme.TextFaint));
            keyBox.Add(new Label(key) { style = { marginLeft = 6, fontSize = 12, color = NotesTheme.TextMuted, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis, whiteSpace = WhiteSpace.NoWrap, flexShrink = 1 } });
            row.Add(keyBox);
            if (isBool && !readOnly)
            {
                var toggle = new Toggle { value = value == "true" };
                toggle.RegisterValueChangedCallback(e => Apply(key, e.newValue ? "true" : "false", false));
                row.Add(toggle);
                row.Add(new VisualElement { style = { flexGrow = 1 } });
            }
            else
            {
                var field = new TextField { value = value, isReadOnly = readOnly, style = { flexGrow = 1, color = NotesTheme.TextNormal } }.WithClass("fn-input", "fn-input--bare");
                if (readOnly) field.style.opacity = 0.6f;
                else
                {
                    field.RegisterCallback<FocusOutEvent>(_ => { if (field.value != m_Note.GetPropertyText(key)) Apply(key, field.value, isList); });
                    field.RegisterCallback<KeyDownEvent>(e =>
                    {
                        if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                        Apply(key, field.value, isList);
                        e.StopPropagation();
                    }, TrickleDown.TrickleDown);
                }
                row.Add(field);
            }

            if (!readOnly)
            {
                row.Add(NotesIcons.IconButton("x", "Remove property", () => { m_Note.RemoveProperty(key); Changed(); }, 14f).WithClass("fn-icon-btn--small"));
            }
            return row;
        }

        private VisualElement BuildAddRow()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 4, marginBottom = 6 } };
            var keyField = new TextField { style = { width = 140, marginRight = 4 } }.WithClass("fn-input");
            keyField.textEdition.placeholder = "Add property";
            var valueField = new TextField { style = { flexGrow = 1 } }.WithClass("fn-input");
            valueField.textEdition.placeholder = "value (a, b for lists)";
            var asList = new Toggle { text = "list", tooltip = "Store as a YAML list", style = { marginLeft = 6, color = NotesTheme.TextMuted } };

            void Add()
            {
                string key = keyField.value?.Trim();
                if (string.IsNullOrEmpty(key) || NoteItem.IsReadOnlyProperty(key)) return;
                Apply(key, valueField.value ?? "", asList.value || IsListKey(key));
            }

            valueField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                Add();
                e.StopPropagation();
            }, TrickleDown.TrickleDown);

            row.Add(keyField);
            row.Add(valueField);
            row.Add(asList);
            row.Add(NotesIcons.IconButton("plus", "Add property", Add));
            return row;
        }

        private static bool IsListKey(string key) =>
            key.Equals("tags", StringComparison.OrdinalIgnoreCase) || key.Equals("aliases", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("cssclasses", StringComparison.OrdinalIgnoreCase);

        private void Apply(string key, string value, bool asList)
        {
            if (m_Note == null) return;
            m_Note.SetProperty(key, value, asList);
            Changed();
        }

        private void Changed()
        {
            OnChanged?.Invoke(m_Note);
            schedule.Execute(Rebuild);
        }
    }
}
