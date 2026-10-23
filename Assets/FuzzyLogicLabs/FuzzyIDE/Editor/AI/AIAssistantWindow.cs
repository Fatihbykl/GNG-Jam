using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using FuzzyLogicLabs.FuzzyIDE.Editor.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.AI
{
    public class AIAssistantWindow : EditorWindow
    {
        public static AIAssistantWindow Instance { get; private set; }
        public static bool IsOpen => Instance != null;

        private AssistantView m_AssistantView;

        [MenuItem("Window/Fuzzy Logic Labs/Fuzzy IDE/AI Assistant", false, 20)]
        public static AIAssistantWindow ShowWindow()
        {
            var window = GetWindow<AIAssistantWindow>("AI Assistant");
            window.minSize = new Vector2(320, 400);
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            Instance = this;
            Texture icon = EditorGUIUtility.IconContent("d_console.infoicon.sml")?.image;
            titleContent = new GUIContent("AI Assistant", icon);
            InitUI();
        }

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        private void InitUI()
        {
            rootVisualElement.Clear();

            m_AssistantView = new AssistantView();
            m_AssistantView.OnApplyCodeRequested += HandleApplyCode;
            m_AssistantView.OnApplyDiffRequested += HandleApplyDiff;
            m_AssistantView.OnCreateFileRequested += HandleCreateFile;
            m_AssistantView.OnGetContextRequested += HandleGetContext;

            rootVisualElement.Add(m_AssistantView);
        }

        public void SendFixError(string error, string lineText)
        {
            if (m_AssistantView != null)
            {
                m_AssistantView.SendDirectRequest($"I have a compilation error:\n{error}\n\nOn this line:\n{lineText}\n\nPlease fix it.");
            }
        }

        private VirtualIDEWindow GetActiveIDEWindow()
        {
            if (VirtualIDEWindow.ActiveInstance != null) return VirtualIDEWindow.ActiveInstance;
            if (EditorWindow.HasOpenInstances<VirtualIDEWindow>())
            {
                return EditorWindow.GetWindow<VirtualIDEWindow>(false, null, false);
            }
            return null;
        }

        private void HandleApplyCode(string code)
        {
            var ide = GetActiveIDEWindow();
            if (ide != null)
            {
                ide.HandleAssistantApplyCode(code);
            }
            else
            {
                Debug.LogWarning("[AI Assistant] No active Virtual IDE window found to apply code to.");
            }
        }

        private void HandleApplyDiff(string path, string searchCode, string newCode, Action onAccept, Action onReject)
        {
            var ide = GetActiveIDEWindow();
            if (ide != null)
            {
                ide.HandleAssistantApplyDiff(path, searchCode, newCode, onAccept, onReject);
            }
            else
            {
                Debug.LogWarning("[AI Assistant] No active Virtual IDE window found to apply diff to.");
                onReject?.Invoke();
            }
        }

        private void HandleCreateFile(string path, string code)
        {
            var ide = GetActiveIDEWindow();
            if (ide != null)
            {
                ide.HandleAssistantCreateFile(path, code);
            }
            else
            {
                try
                {
                    string directory = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.CreateDirectory(directory);
                    }
                    System.IO.File.WriteAllText(path, code);
                    AssetDatabase.ImportAsset(path);
                    // Debug.Log($"[AI Assistant] Created file: {path}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AI Assistant] Failed to create file {path}: {ex.Message}");
                }
            }
        }

        private string HandleGetContext()
        {
            var ide = GetActiveIDEWindow();
            if (ide != null)
            {
                return ide.HandleAssistantGetContext();
            }
            return "";
        }
    }
}
