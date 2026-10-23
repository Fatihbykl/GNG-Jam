using System;
using System.IO;
using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.UI
{
    public class CreateAssetDialog : EditorWindow
    {
        public enum DialogMode
        {
            CreateScript,
            CreateFolder,
            CreateFile,
            Rename
        }

        private DialogMode m_Mode;
        private string m_TargetFolderPath = "Assets";
        private string m_TargetAssetPath = "";
        private string m_NameInput = "";
        private ScriptTemplateType m_ScriptTemplate = ScriptTemplateType.MonoBehaviour;
        private string m_Namespace = "";
        private AssetCreationType m_FileCreationType = AssetCreationType.CSharpScript;
        private string m_ErrorMessage = "";
        private bool m_FocusSet = false;

        public static void OpenCreateScript(string folderPath)
        {
            var window = CreateInstance<CreateAssetDialog>();
            window.titleContent = new GUIContent("Create C# Script");
            window.m_Mode = DialogMode.CreateScript;
            window.m_TargetFolderPath = string.IsNullOrEmpty(folderPath) ? "Assets" : folderPath;
            window.m_NameInput = "NewBehaviourScript";
            window.m_ScriptTemplate = ScriptTemplateType.MonoBehaviour;
            try { window.m_Namespace = ProjectAssetOperations.SuggestNamespace(window.m_TargetFolderPath); }
            catch (System.Exception) { window.m_Namespace = ""; }
            window.minSize = new Vector2(380, 232);
            window.maxSize = new Vector2(450, 232);
            window.ShowUtility();
        }

        public static void OpenCreateFolder(string parentFolderPath)
        {
            var window = CreateInstance<CreateAssetDialog>();
            window.titleContent = new GUIContent("Create Folder");
            window.m_Mode = DialogMode.CreateFolder;
            window.m_TargetFolderPath = string.IsNullOrEmpty(parentFolderPath) ? "Assets" : parentFolderPath;
            window.m_NameInput = "New Folder";
            window.minSize = new Vector2(380, 170);
            window.maxSize = new Vector2(450, 170);
            window.ShowUtility();
        }

        public static void OpenCreateFile(string folderPath)
        {
            var window = CreateInstance<CreateAssetDialog>();
            window.titleContent = new GUIContent("Create File");
            window.m_Mode = DialogMode.CreateFile;
            window.m_TargetFolderPath = string.IsNullOrEmpty(folderPath) ? "Assets" : folderPath;
            window.m_NameInput = "NewFile";
            window.m_FileCreationType = AssetCreationType.UnlitShader;
            window.minSize = new Vector2(380, 210);
            window.maxSize = new Vector2(450, 210);
            window.ShowUtility();
        }

        public static void OpenRename(string assetPath)
        {
            var window = CreateInstance<CreateAssetDialog>();
            window.titleContent = new GUIContent("Rename Asset");
            window.m_Mode = DialogMode.Rename;
            window.m_TargetAssetPath = assetPath;
            window.m_TargetFolderPath = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            window.m_NameInput = Path.GetFileNameWithoutExtension(assetPath);
            window.minSize = new Vector2(380, 170);
            window.maxSize = new Vector2(450, 170);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            Event evt = Event.current;
            if (evt.type == EventType.KeyDown)
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    ExecuteAction();
                    evt.Use();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    Close();
                    evt.Use();
                }
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField($"Location: {m_TargetFolderPath}", EditorStyles.miniLabel);
            EditorGUILayout.Space(5);

            switch (m_Mode)
            {
                case DialogMode.CreateScript:
                    DrawCreateScriptGUI();
                    break;
                case DialogMode.CreateFolder:
                    DrawCreateFolderGUI();
                    break;
                case DialogMode.CreateFile:
                    DrawCreateFileGUI();
                    break;
                case DialogMode.Rename:
                    DrawRenameGUI();
                    break;
            }

            ValidateInput();

            if (!string.IsNullOrEmpty(m_ErrorMessage))
            {
                EditorGUILayout.Space(5);
                GUI.color = new Color(1f, 0.4f, 0.4f);
                EditorGUILayout.LabelField($"⚠️ {m_ErrorMessage}", EditorStyles.wordWrappedMiniLabel);
                GUI.color = Color.white;
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", GUILayout.Width(80), GUILayout.Height(24)))
            {
                Close();
            }

            EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(m_ErrorMessage) || string.IsNullOrWhiteSpace(m_NameInput));
            string actionText = m_Mode == DialogMode.Rename ? "Rename" : "Create";
            if (GUILayout.Button(actionText, GUILayout.Width(80), GUILayout.Height(24)))
            {
                ExecuteAction();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(10);
        }

        private void DrawCreateScriptGUI()
        {
            GUI.SetNextControlName("NameInputField");
            m_NameInput = EditorGUILayout.TextField("Script Name", m_NameInput);
            m_ScriptTemplate = (ScriptTemplateType)EditorGUILayout.EnumPopup("Template", m_ScriptTemplate);
            m_Namespace = EditorGUILayout.TextField(new GUIContent("Namespace", "From the folder's asmdef root namespace; leave empty for none."), m_Namespace);

            FocusFieldOnce();
        }

        private void DrawCreateFolderGUI()
        {
            GUI.SetNextControlName("NameInputField");
            m_NameInput = EditorGUILayout.TextField("Folder Name", m_NameInput);

            FocusFieldOnce();
        }

        private void DrawCreateFileGUI()
        {
            GUI.SetNextControlName("NameInputField");
            m_NameInput = EditorGUILayout.TextField("File Name", m_NameInput);
            m_FileCreationType = (AssetCreationType)EditorGUILayout.EnumPopup("File Type", m_FileCreationType);

            FocusFieldOnce();
        }

        private void DrawRenameGUI()
        {
            EditorGUILayout.LabelField($"Current: {Path.GetFileName(m_TargetAssetPath)}", EditorStyles.boldLabel);
            GUI.SetNextControlName("NameInputField");
            m_NameInput = EditorGUILayout.TextField("New Name", m_NameInput);

            FocusFieldOnce();
        }

        private void FocusFieldOnce()
        {
            if (!m_FocusSet)
            {
                EditorGUI.FocusTextInControl("NameInputField");
                m_FocusSet = true;
            }
        }

        private void ValidateInput()
        {
            m_ErrorMessage = "";
            if (string.IsNullOrWhiteSpace(m_NameInput))
            {
                m_ErrorMessage = "Name cannot be empty.";
                return;
            }

            if (m_Mode == DialogMode.CreateScript)
            {
                if (!ProjectAssetOperations.IsValidCSharpIdentifier(m_NameInput))
                {
                    m_ErrorMessage = "Not a valid C# identifier (cannot contain spaces or symbols, cannot be a keyword).";
                    return;
                }

                string candidate = Path.Combine(m_TargetFolderPath, m_NameInput + ".cs").Replace('\\', '/');
                if (File.Exists(candidate))
                {
                    m_ErrorMessage = $"A script named '{m_NameInput}.cs' already exists here.";
                    return;
                }
            }
            else if (m_Mode == DialogMode.CreateFolder)
            {
                if (!ProjectAssetOperations.IsValidFileName(m_NameInput, out string err))
                {
                    m_ErrorMessage = err;
                    return;
                }

                string candidate = Path.Combine(m_TargetFolderPath, m_NameInput).Replace('\\', '/');
                if (AssetDatabase.IsValidFolder(candidate) || Directory.Exists(candidate))
                {
                    m_ErrorMessage = $"Folder '{m_NameInput}' already exists.";
                    return;
                }
            }
            else if (m_Mode == DialogMode.CreateFile)
            {
                if (!ProjectAssetOperations.IsValidFileName(m_NameInput, out string err))
                {
                    m_ErrorMessage = err;
                    return;
                }
            }
            else if (m_Mode == DialogMode.Rename)
            {
                if (!ProjectAssetOperations.IsValidFileName(m_NameInput, out string err))
                {
                    m_ErrorMessage = err;
                    return;
                }
            }
        }

        private void ExecuteAction()
        {
            ValidateInput();
            if (!string.IsNullOrEmpty(m_ErrorMessage)) return;

            switch (m_Mode)
            {
                case DialogMode.CreateScript:
                    if (ProjectAssetOperations.CreateScriptAsset(m_TargetFolderPath, m_NameInput.Trim(), m_ScriptTemplate, m_Namespace, out string createdScript, out string scriptErr))
                    {
                        Close();
                        if (VirtualIDEWindow.ActiveInstance != null)
                        {
                            VirtualIDEWindow.ActiveInstance.LoadFileAndJumpToLine(createdScript, 0);
                        }
                    }
                    else
                    {
                        m_ErrorMessage = scriptErr;
                    }
                    break;

                case DialogMode.CreateFolder:
                    if (ProjectAssetOperations.CreateFolder(m_TargetFolderPath, m_NameInput.Trim(), out _, out string folderErr))
                    {
                        Close();
                        VirtualIDEWindow.ActiveInstance?.RefreshExplorerHierarchy();
                    }
                    else
                    {
                        m_ErrorMessage = folderErr;
                    }
                    break;

                case DialogMode.CreateFile:
                    string ext = ".txt";
                    string content = "";
                    if (m_FileCreationType == AssetCreationType.UnlitShader)
                    {
                        ext = ".shader";
                        content = ProjectAssetOperations.GenerateShaderTemplate(m_NameInput.Trim());
                    }
                    else if (m_FileCreationType == AssetCreationType.AssemblyDefinition)
                    {
                        ext = ".asmdef";
                        content = ProjectAssetOperations.GenerateAsmdefTemplate(m_NameInput.Trim());
                    }
                    else if (m_FileCreationType == AssetCreationType.JsonFile)
                    {
                        ext = ".json";
                        content = "{\n\n}";
                    }
                    else if (m_FileCreationType == AssetCreationType.MarkdownFile)
                    {
                        ext = ".md";
                        content = $"# {m_NameInput.Trim()}\n";
                    }

                    if (ProjectAssetOperations.CreateGenericFile(m_TargetFolderPath, m_NameInput.Trim(), ext, content, out string createdFile, out string fileErr))
                    {
                        Close();
                        if (VirtualIDEWindow.ActiveInstance != null)
                        {
                            VirtualIDEWindow.ActiveInstance.LoadFileAndJumpToLine(createdFile, 0);
                        }
                    }
                    else
                    {
                        m_ErrorMessage = fileErr;
                    }
                    break;

                case DialogMode.Rename:
                    if (ProjectAssetOperations.RenameAsset(m_TargetAssetPath, m_NameInput.Trim(), out string newPath, out string renameErr))
                    {
                        Close();
                        VirtualIDEWindow.ActiveInstance?.OnAssetRenamedInProject(m_TargetAssetPath, newPath);
                    }
                    else
                    {
                        m_ErrorMessage = renameErr;
                    }
                    break;
            }
        }
    }
}
