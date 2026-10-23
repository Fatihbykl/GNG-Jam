using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    [Serializable]
    public struct AutoAssignIntent
    {
        public string ScriptPath;
        public string VariableName;
        public int DraggedInstanceID;
        public int TargetGameObjectInstanceID;
    }

    [Serializable]
    public class AutoAssignIntentWrapper
    {
        public System.Collections.Generic.List<AutoAssignIntent> Intents = new System.Collections.Generic.List<AutoAssignIntent>();
    }

    public static class SmartDragDropPostprocessor
    {
        private const string PREF_KEY = "VirtualIDE_AutoAssignIntent";

        public static void RegisterIntent(AutoAssignIntent intent)
        {
            AutoAssignIntentWrapper wrapper = new AutoAssignIntentWrapper();
            if (EditorPrefs.HasKey(PREF_KEY))
            {
                string oldJson = EditorPrefs.GetString(PREF_KEY);
                try { wrapper = JsonUtility.FromJson<AutoAssignIntentWrapper>(oldJson) ?? wrapper; } catch { }
            }
            wrapper.Intents.Add(intent);
            string json = JsonUtility.ToJson(wrapper);
            EditorPrefs.SetString(PREF_KEY, json);
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            if (!EditorPrefs.HasKey(PREF_KEY)) return;

            string json = EditorPrefs.GetString(PREF_KEY);
            EditorPrefs.DeleteKey(PREF_KEY);

            if (string.IsNullOrEmpty(json)) return;

            AutoAssignIntentWrapper wrapper;
            try
            {
                wrapper = JsonUtility.FromJson<AutoAssignIntentWrapper>(json);
            }
            catch
            {
                return;
            }

            if (wrapper == null || wrapper.Intents == null) return;

#pragma warning disable 0618
            MonoBehaviour[] allBehaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
#pragma warning restore 0618

            foreach (var intent in wrapper.Intents)
            {
                if (string.IsNullOrEmpty(intent.VariableName)) continue;

                UnityEngine.Object draggedObject = null;
                var instanceIdToObject = typeof(EditorUtility).GetMethod("InstanceIDToObject", new Type[] { typeof(int) });
                if (instanceIdToObject != null)
                {
                    draggedObject = (UnityEngine.Object)instanceIdToObject.Invoke(null, new object[] { intent.DraggedInstanceID });
                }
                if (draggedObject == null) continue;

                foreach (var behaviour in allBehaviours)
                {
                    if (behaviour == null) continue;

                    MonoScript script = MonoScript.FromMonoBehaviour(behaviour);
                    if (script == null) continue;

                    string assetPath = AssetDatabase.GetAssetPath(script);
                    if (Path.GetFullPath(assetPath).Equals(Path.GetFullPath(intent.ScriptPath), StringComparison.OrdinalIgnoreCase))
                    {
                        SerializedObject so = new SerializedObject(behaviour);
                        SerializedProperty prop = so.FindProperty(intent.VariableName);

                        if (prop != null)
                        {
                            prop.objectReferenceValue = draggedObject;
                            so.ApplyModifiedProperties();
                            // Debug.Log($"[Smart Drag&Drop] Auto-assigned {draggedObject.name} to {intent.VariableName} on {behaviour.gameObject.name}");
                        }
                    }
                }
            }
        }
    }
}