using System;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>Small modal text prompt (used where an inline field is not available yet).</summary>
    public static class EditorInputDialog
    {
        public static string Show(string title, string description, string defaultValue = "")
        {
            // Simple prompt using Modal EditorWindow
            string result = defaultValue;
            var win = ScriptableObject.CreateInstance<InputDialogWindow>();
            win.Init(title, description, defaultValue, res => result = res);
            win.ShowModal();
            return result;
        }

        private class InputDialogWindow : EditorWindow
        {
            private string m_Desc;
            private string m_Value;
            private Action<string> m_OnComplete;
            private bool m_Focused;

            public void Init(string title, string desc, string def, Action<string> onComplete)
            {
                titleContent = new GUIContent(title);
                m_Desc = desc;
                m_Value = def;
                m_OnComplete = onComplete;
                minSize = new Vector2(300, 100);
                maxSize = new Vector2(400, 120);
            }

            private void OnGUI()
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField(m_Desc);
                GUI.SetNextControlName("value");
                m_Value = EditorGUILayout.TextField(m_Value);
                if (!m_Focused)
                {
                    EditorGUI.FocusTextInControl("value");
                    m_Focused = true;
                }

                bool submit = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                bool cancel = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape;

                EditorGUILayout.Space(10);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Cancel") || cancel) Close();
                if (GUILayout.Button("OK") || submit)
                {
                    m_OnComplete?.Invoke(m_Value);
                    Close();
                }
                EditorGUILayout.EndHorizontal();
            }
        }
    }
}
