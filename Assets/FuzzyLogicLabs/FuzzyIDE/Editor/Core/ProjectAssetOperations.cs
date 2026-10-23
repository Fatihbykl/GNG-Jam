using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public enum ScriptTemplateType
    {
        MonoBehaviour,
        PlainClass,
        Interface,
        ScriptableObject,
        CustomEditor,
        EditorWindow,
        PropertyDrawer,
        Enum,
        Struct
    }

    public enum AssetCreationType
    {
        CSharpScript,
        Folder,
        UnlitShader,
        AssemblyDefinition,
        JsonFile,
        MarkdownFile,
        TextFile
    }

    public static class ProjectAssetOperations
    {
        public static event Action<string, string> OnAssetRenamed;
        public static event Action<string> OnAssetDeleted;
        public static event Action<string> OnAssetCreated;

        private static readonly HashSet<string> s_CSharpKeywords = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
            "virtual", "void", "volatile", "while"
        };

        public static bool IsValidCSharpIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (s_CSharpKeywords.Contains(name)) return false;
            return Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");
        }

        public static bool IsValidFileName(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "File name cannot be empty.";
                return false;
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            if (name.IndexOfAny(invalidChars) >= 0)
            {
                error = "File name contains invalid characters.";
                return false;
            }

            return true;
        }

        public static string GenerateScriptTemplate(string className, ScriptTemplateType templateType, string namespaceName)
        {
            return WrapInNamespace(GenerateScriptTemplate(className, templateType), namespaceName);
        }

        public static string GenerateScriptTemplate(string className, ScriptTemplateType templateType)
        {
            switch (templateType)
            {
                case ScriptTemplateType.PlainClass:
                    return $"using System;\nusing UnityEngine;\n\npublic class {className}\n{{\n    public {className}()\n    {{\n        \n    }}\n}}\n";

                case ScriptTemplateType.Interface:
                    return $"using System;\n\npublic interface I{className.TrimStart('I')}\n{{\n    void Execute();\n}}\n";

                case ScriptTemplateType.ScriptableObject:
                    return $"using UnityEngine;\n\n[CreateAssetMenu(fileName = \"{className}\", menuName = \"Scriptable Objects/{className}\")]\npublic class {className} : ScriptableObject\n{{\n    \n}}\n";

                case ScriptTemplateType.CustomEditor:
                    return $"using UnityEngine;\nusing UnityEditor;\n\n[CustomEditor(typeof({InspectedType(className, "Editor", "MonoBehaviour")}))]\npublic class {className} : Editor\n{{\n    public override void OnInspectorGUI()\n    {{\n        base.OnInspectorGUI();\n    }}\n}}\n";

                case ScriptTemplateType.EditorWindow:
                    return $"using UnityEditor;\nusing UnityEngine;\nusing UnityEngine.UIElements;\n\npublic class {className} : EditorWindow\n{{\n    [MenuItem(\"Window/{ObjectNames.NicifyVariableName(className)}\")]\n    public static void Open()\n    {{\n        GetWindow<{className}>(\"{ObjectNames.NicifyVariableName(className)}\");\n    }}\n\n    public void CreateGUI()\n    {{\n        rootVisualElement.Add(new Label(\"{className}\"));\n    }}\n}}\n";

                case ScriptTemplateType.PropertyDrawer:
                    return $"using UnityEditor;\nusing UnityEngine;\n\n[CustomPropertyDrawer(typeof({InspectedType(className, "Drawer", "PropertyAttribute")}))]\npublic class {className} : PropertyDrawer\n{{\n    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)\n    {{\n        EditorGUI.PropertyField(position, property, label, true);\n    }}\n\n    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)\n    {{\n        return EditorGUI.GetPropertyHeight(property, label, true);\n    }}\n}}\n";

                case ScriptTemplateType.Enum:
                    return $"public enum {className}\n{{\n    None,\n}}\n";

                case ScriptTemplateType.Struct:
                    return $"using System;\nusing UnityEngine;\n\n[Serializable]\npublic struct {className}\n{{\n    \n}}\n";

                case ScriptTemplateType.MonoBehaviour:
                default:
                    return $"using UnityEngine;\n\npublic class {className} : MonoBehaviour\n{{\n    void Start()\n    {{\n        \n    }}\n\n    void Update()\n    {{\n        \n    }}\n}}\n";
            }
        }

        // PlayerEditor -> typeof(Player); falls back when the name has no meaningful prefix.
        private static string InspectedType(string className, string suffix, string fallback)
        {
            return className.Length > suffix.Length && className.EndsWith(suffix, StringComparison.Ordinal)
                ? className.Substring(0, className.Length - suffix.Length) : fallback;
        }

        /// <summary>Puts everything after the leading using directives into <paramref name="namespaceName"/>, indented.</summary>
        public static string WrapInNamespace(string code, string namespaceName)
        {
            if (string.IsNullOrWhiteSpace(namespaceName)) return code;
            var lines = code.Replace("\r\n", "\n").Split('\n');
            int bodyStart = 0;
            while (bodyStart < lines.Length && (lines[bodyStart].StartsWith("using ", StringComparison.Ordinal) || (lines[bodyStart].Length == 0 && bodyStart > 0 && lines[bodyStart - 1].StartsWith("using ", StringComparison.Ordinal))))
                bodyStart++;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < bodyStart; i++) sb.Append(lines[i]).Append('\n');
            sb.Append("namespace ").Append(namespaceName.Trim()).Append("\n{\n");
            int end = lines.Length;
            while (end > bodyStart && lines[end - 1].Length == 0) end--;
            for (int i = bodyStart; i < end; i++)
                sb.Append(lines[i].Length > 0 ? "    " + lines[i] : "").Append('\n');
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Namespace for a new script in <paramref name="folderPath"/>: the root namespace of the governing asmdef (or
        /// the project's root namespace setting for Assembly-CSharp) followed by the sub-folders below the asmdef.
        /// </summary>
        public static string SuggestNamespace(string folderPath)
        {
            folderPath = (folderPath ?? "Assets").Replace('\\', '/').TrimEnd('/');
            for (string dir = folderPath; !string.IsNullOrEmpty(dir); dir = ParentFolder(dir))
            {
                string asmdef = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.asmdef").FirstOrDefault() : null;
                if (asmdef == null) continue;
                string root = ReadRootNamespace(File.ReadAllText(asmdef));
                return ComposeNamespace(root, dir, folderPath);
            }
            return ComposeNamespace(UnityEditor.EditorSettings.projectGenerationRootNamespace, "Assets", folderPath);
        }

        private static string ParentFolder(string dir)
        {
            int slash = dir.LastIndexOf('/');
            return slash > 0 ? dir.Substring(0, slash) : null;
        }

        /// <summary>Root namespace plus the folders between <paramref name="baseFolder"/> and <paramref name="folderPath"/>.</summary>
        public static string ComposeNamespace(string rootNamespace, string baseFolder, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(rootNamespace)) return "";
            var parts = new List<string> { rootNamespace.Trim() };
            string relative = folderPath.Length > baseFolder.Length && folderPath.StartsWith(baseFolder + "/", StringComparison.Ordinal)
                ? folderPath.Substring(baseFolder.Length + 1) : "";
            foreach (var segment in relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Grouping folders don't name a namespace level.
                if (segment == "Scripts" || segment == "Runtime" || segment == "Editor") continue;
                string identifier = new string(segment.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                if (identifier.Length == 0) continue;
                if (char.IsDigit(identifier[0])) identifier = "_" + identifier;
                parts.Add(identifier);
            }
            return string.Join(".", parts);
        }

        private static string ReadRootNamespace(string asmdefJson)
        {
            var match = System.Text.RegularExpressions.Regex.Match(asmdefJson, "\"rootNamespace\"\\s*:\\s*\"(?<ns>[^\"]*)\"");
            return match.Success ? match.Groups["ns"].Value : "";
        }

        public static string GenerateShaderTemplate(string shaderName)
        {
            return $@"Shader ""Custom/{shaderName}""
{{
    Properties
    {{
        _MainTex (""Texture"", 2D) = ""white"" {{}}
        _Color (""Color"", Color) = (1,1,1,1)
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" }}
        LOD 100

        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            struct appdata
            {{
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            }};

            struct v2f
            {{
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            }};

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert (appdata v)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }}

            fixed4 frag (v2f i) : SV_Target
            {{
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                return col;
            }}
            ENDCG
        }}
    }}
}}
";
        }

        public static string GenerateAsmdefTemplate(string assemblyName)
        {
            return $@"{{
    ""name"": ""{assemblyName}"",
    ""rootNamespace"": """",
    ""references"": [],
    ""includePlatforms"": [],
    ""excludePlatforms"": [],
    ""allowUnsafeCode"": false,
    ""overrideReferences"": false,
    ""precompiledReferences"": [],
    ""autoReferenced"": true,
    ""defineConstraints"": [],
    ""versionDefines"": [],
    ""noEngineReferences"": false
}}";
        }

        public static bool CreateScriptAsset(string folderPath, string scriptName, ScriptTemplateType templateType, out string createdAssetPath, out string errorMessage)
        {
            return CreateScriptAsset(folderPath, scriptName, templateType, null, out createdAssetPath, out errorMessage);
        }

        public static bool CreateScriptAsset(string folderPath, string scriptName, ScriptTemplateType templateType, string namespaceName, out string createdAssetPath, out string errorMessage)
        {
            createdAssetPath = null;
            errorMessage = null;

            if (!IsValidCSharpIdentifier(scriptName))
            {
                errorMessage = $"'{scriptName}' is not a valid C# identifier.";
                return false;
            }

            folderPath = EnsureValidFolderPath(folderPath);
            string targetPath = Path.Combine(folderPath, scriptName + ".cs").Replace('\\', '/');

            if (File.Exists(targetPath))
            {
                errorMessage = $"File '{targetPath}' already exists.";
                return false;
            }

            string code = GenerateScriptTemplate(scriptName, templateType, namespaceName);

            try
            {
                string fullDiskPath = Path.GetFullPath(targetPath);
                string dir = Path.GetDirectoryName(fullDiskPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(fullDiskPath, code);

                var compSettings = CodeIDESettings.Current?.Compilation;
                bool shouldDefer = compSettings != null && compSettings.DeferScriptCreation && compSettings.Policy != CompilationPolicy.UnityDefault;

                if (!shouldDefer)
                {
                    AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                    AssetDatabase.Refresh();
                }
                else
                {
                    CompilationManager.MarkFileModified(targetPath);
                }

                createdAssetPath = targetPath;
                OnAssetCreated?.Invoke(createdAssetPath);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool CreateFolder(string parentFolderPath, string newFolderName, out string createdFolderPath, out string errorMessage)
        {
            createdFolderPath = null;
            errorMessage = null;

            if (!IsValidFileName(newFolderName, out errorMessage))
            {
                return false;
            }

            parentFolderPath = EnsureValidFolderPath(parentFolderPath);
            string expectedPath = Path.Combine(parentFolderPath, newFolderName).Replace('\\', '/');

            if (AssetDatabase.IsValidFolder(expectedPath))
            {
                errorMessage = $"Folder '{expectedPath}' already exists.";
                return false;
            }

            try
            {
                string guid = AssetDatabase.CreateFolder(parentFolderPath, newFolderName);
                if (string.IsNullOrEmpty(guid))
                {
                    errorMessage = "Failed to create folder via AssetDatabase.";
                    return false;
                }

                createdFolderPath = AssetDatabase.GUIDToAssetPath(guid);
                AssetDatabase.Refresh();
                OnAssetCreated?.Invoke(createdFolderPath);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool CreateGenericFile(string folderPath, string fileName, string extension, string content, out string createdAssetPath, out string errorMessage)
        {
            createdAssetPath = null;
            errorMessage = null;

            if (!IsValidFileName(fileName, out errorMessage))
            {
                return false;
            }

            if (!extension.StartsWith(".")) extension = "." + extension;
            if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                fileName += extension;
            }

            folderPath = EnsureValidFolderPath(folderPath);
            string targetPath = Path.Combine(folderPath, fileName).Replace('\\', '/');

            if (File.Exists(targetPath))
            {
                errorMessage = $"File '{targetPath}' already exists.";
                return false;
            }

            try
            {
                string fullDiskPath = Path.GetFullPath(targetPath);
                string dir = Path.GetDirectoryName(fullDiskPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(fullDiskPath, content ?? "");
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();

                createdAssetPath = targetPath;
                OnAssetCreated?.Invoke(createdAssetPath);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool RenameAsset(string assetPath, string newName, out string newAssetPath, out string errorMessage)
        {
            newAssetPath = null;
            errorMessage = null;

            if (string.IsNullOrEmpty(assetPath))
            {
                errorMessage = "Source asset path is null or empty.";
                return false;
            }

            if (!IsValidFileName(newName, out errorMessage))
            {
                return false;
            }

            string oldNameWithoutExt = Path.GetFileNameWithoutExtension(assetPath);
            string ext = Path.GetExtension(assetPath);
            bool isFolder = AssetDatabase.IsValidFolder(assetPath);

            // Strip extension from newName if user typed it
            string cleanNewName = newName;
            if (!isFolder && cleanNewName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                cleanNewName = cleanNewName.Substring(0, cleanNewName.Length - ext.Length);
            }

            if (cleanNewName == oldNameWithoutExt)
            {
                newAssetPath = assetPath;
                return true;
            }

            // If it's a C# script, rename the class name inside if it matched the old file name
            if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) && IsValidCSharpIdentifier(cleanNewName))
            {
                string fullDisk = Path.GetFullPath(assetPath);
                if (File.Exists(fullDisk))
                {
                    try
                    {
                        string code = File.ReadAllText(fullDisk);
                        string updatedCode = Regex.Replace(code, $@"\b(class|struct|interface)\s+{Regex.Escape(oldNameWithoutExt)}\b", $"$1 {cleanNewName}");
                        if (updatedCode != code)
                        {
                            File.WriteAllText(fullDisk, updatedCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[FuzzyIDE] Could not update class name inside script: {ex.Message}");
                    }
                }
            }

            string resultMsg = AssetDatabase.RenameAsset(assetPath, cleanNewName);
            if (!string.IsNullOrEmpty(resultMsg))
            {
                errorMessage = resultMsg;
                return false;
            }

            string parentDir = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            newAssetPath = isFolder 
                ? $"{parentDir}/{cleanNewName}" 
                : $"{parentDir}/{cleanNewName}{ext}";

            AssetDatabase.Refresh();
            OnAssetRenamed?.Invoke(assetPath, newAssetPath);
            return true;
        }

        public static bool DeleteAsset(string assetPath, bool confirmWithDialog = true)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;

            string fileName = Path.GetFileName(assetPath);
            if (confirmWithDialog)
            {
                bool confirmed = EditorUtility.DisplayDialog(
                    "Delete Asset",
                    $"Are you sure you want to delete '{fileName}'?\n\nThis will move the asset to your system Trash.",
                    "Delete",
                    "Cancel"
                );

                if (!confirmed) return false;
            }

            bool success = AssetDatabase.MoveAssetToTrash(assetPath);
            if (success)
            {
                AssetDatabase.Refresh();
                OnAssetDeleted?.Invoke(assetPath);
            }
            return success;
        }

        public static bool DuplicateAsset(string assetPath, out string newAssetPath, out string errorMessage)
        {
            newAssetPath = null;
            errorMessage = null;

            if (string.IsNullOrEmpty(assetPath))
            {
                errorMessage = "Source asset path is null or empty.";
                return false;
            }

            string uniquePath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            bool success = AssetDatabase.CopyAsset(assetPath, uniquePath);
            if (!success)
            {
                errorMessage = $"Failed to copy asset '{assetPath}'.";
                return false;
            }

            AssetDatabase.Refresh();
            newAssetPath = uniquePath;
            OnAssetCreated?.Invoke(newAssetPath);
            return true;
        }

        public static void RevealInExplorer(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            string fullPath = Path.GetFullPath(assetPath);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                EditorUtility.RevealInFinder(fullPath);
            }
        }

        public static void PingInProjectWindow(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
            }
        }

        private static string EnsureValidFolderPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "Assets";
            path = path.Replace('\\', '/').TrimEnd('/');
            if (!path.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                path = "Assets";
            }
            if (!AssetDatabase.IsValidFolder(path) && !Directory.Exists(path))
            {
                path = "Assets";
            }
            return path;
        }
    }
}
