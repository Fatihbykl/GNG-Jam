using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>
    /// Keeps a <see cref="UnityProjectSnapshot"/> of tags, layers, scenes, input axes, animator names, Resources paths
    /// and shaders. Unity APIs are main-thread only, so the snapshot is rebuilt here (on load and shortly after the
    /// project changes) and handed to analysis threads as an immutable object.
    /// </summary>
    [InitializeOnLoad]
    public static class UnityProjectData
    {
        private static volatile UnityProjectSnapshot s_Current = UnityProjectSnapshot.Empty;
        private static double s_RefreshAt = -1;

        public static UnityProjectSnapshot Current => s_Current;

        static UnityProjectData()
        {
            EditorApplication.delayCall += Refresh;
            EditorApplication.projectChanged += ScheduleRefresh;
            EditorApplication.update += OnUpdate;
        }

        // Asset imports arrive in bursts; rebuild once things settle.
        private static void ScheduleRefresh() => s_RefreshAt = EditorApplication.timeSinceStartup + 1.5;

        private static void OnUpdate()
        {
            if (s_RefreshAt < 0 || EditorApplication.timeSinceStartup < s_RefreshAt) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            s_RefreshAt = -1;
            Refresh();
        }

        public static void Refresh()
        {
            var snapshot = new UnityProjectSnapshot();
            Try(() => snapshot.Tags = InternalEditorUtility.tags.ToArray());
            Try(() => snapshot.Layers = InternalEditorUtility.layers.Where(l => !string.IsNullOrEmpty(l)).ToArray());
            Try(() => snapshot.SortingLayers = SortingLayer.layers.Select(l => l.name).ToArray());
            Try(() =>
            {
                var scenes = EditorBuildSettings.scenes.Where(s => s.enabled && !string.IsNullOrEmpty(s.path)).ToArray();
                snapshot.ScenePaths = scenes.Select(s => s.path).ToArray();
                snapshot.SceneNames = scenes.Select(s => Path.GetFileNameWithoutExtension(s.path)).Distinct().ToArray();
            });
            Try(() => snapshot.InputAxes = ReadInputAxes());
            Try(() => ReadAnimatorNames(snapshot));
            Try(() => snapshot.ResourcePaths = ReadResourcePaths());
            Try(() => snapshot.Shaders = ShaderUtil.GetAllShaderInfo().Select(s => s.name).Where(n => !n.StartsWith("Hidden/", StringComparison.Ordinal)).Distinct().ToArray());
            s_Current = snapshot;
        }

        private static void Try(Action read)
        {
            try { read(); }
            catch (Exception) { /* leave that list empty: missing data only disables completion/validation for it */ }
        }

        private static string[] ReadInputAxes()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset");
            if (assets == null || assets.Length == 0) return Array.Empty<string>();
            var axes = new SerializedObject(assets[0]).FindProperty("m_Axes");
            if (axes == null || !axes.isArray) return Array.Empty<string>();
            var names = new List<string>();
            for (int i = 0; i < axes.arraySize; i++)
            {
                string name = axes.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name")?.stringValue;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names.Distinct().ToArray();
        }

        private static void ReadAnimatorNames(UnityProjectSnapshot snapshot)
        {
            var states = new HashSet<string>();
            var parameters = new HashSet<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:AnimatorController"))
            {
                var controller = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(AssetDatabase.GUIDToAssetPath(guid));
                if (controller == null) continue;
                foreach (var parameter in controller.parameters) parameters.Add(parameter.name);
                foreach (var layer in controller.layers) CollectStates(layer.stateMachine, states);
            }
            snapshot.AnimatorStates = states.ToArray();
            snapshot.AnimatorParameters = parameters.ToArray();
        }

        private static void CollectStates(UnityEditor.Animations.AnimatorStateMachine machine, HashSet<string> states)
        {
            if (machine == null) return;
            foreach (var child in machine.states) states.Add(child.state.name);
            foreach (var sub in machine.stateMachines) CollectStates(sub.stateMachine, states);
        }

        private static string[] ReadResourcePaths()
        {
            var paths = new HashSet<string>();
            const string marker = "/Resources/";
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                int index = path.LastIndexOf(marker, StringComparison.Ordinal);
                if (index < 0 || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                string relative = path.Substring(index + marker.Length);
                if (relative.Length == 0 || AssetDatabase.IsValidFolder(path)) continue;
                int dot = relative.LastIndexOf('.');
                if (dot > relative.LastIndexOf('/')) relative = relative.Substring(0, dot);
                paths.Add(relative);
            }
            return paths.ToArray();
        }
    }
}
