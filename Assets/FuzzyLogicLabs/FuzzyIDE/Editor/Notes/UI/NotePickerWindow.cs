using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI
{
    /// <summary>
    /// A <see cref="NotePalette"/> in its own drop-down window, so the code editor, Inspector and menus can
    /// offer note pickers without the Fuzzy Notes window being open. Closes when it loses focus.
    /// </summary>
    public class NotePickerWindow : EditorWindow
    {
        private const float Width = 560f;
        private const float Height = 380f;

        private string m_Placeholder;
        private string m_Footer;
        private Func<string, List<PaletteItem>> m_Provider;
        private Action<string, bool> m_OnSubmitText;
        private NotePalette m_Palette;
        private bool m_Closing;

        /// <summary>Opens a picker centered over the focused editor window (or the main window).</summary>
        public static void Open(string placeholder, string footer, Func<string, List<PaletteItem>> provider, Action<string, bool> onSubmitText = null)
        {
            var window = CreateInstance<NotePickerWindow>();
            window.m_Placeholder = placeholder;
            window.m_Footer = footer;
            window.m_Provider = provider;
            window.m_OnSubmitText = onSubmitText;

            Rect host = focusedWindow != null ? focusedWindow.position : EditorGUIUtility.GetMainWindowPosition();
            var anchor = new Rect(host.x + (host.width - Width) / 2f, host.y + Mathf.Min(80f, host.height * 0.15f), Width, 0f);
            window.ShowAsDropDown(anchor, new Vector2(Width, Height));
        }

        private void CreateGUI()
        {
            NotesTheme.Apply(rootVisualElement);
            rootVisualElement.style.backgroundColor = NotesTheme.PopupBackground;
            m_Palette = new NotePalette();
            m_Palette.SetEmbedded();
            m_Palette.Closed += () =>
            {
                if (m_Closing) return;
                m_Closing = true;
                // Let the chosen action run first; it may open other windows.
                EditorApplication.delayCall += () => { if (this != null) Close(); };
            };
            rootVisualElement.Add(m_Palette);

            if (m_Provider == null)
            {
                // Domain reload destroyed the callbacks; nothing sensible to show.
                EditorApplication.delayCall += () => { if (this != null) Close(); };
                return;
            }
            m_Palette.Open(m_Placeholder, m_Footer, m_Provider, m_OnSubmitText);
        }
    }
}
