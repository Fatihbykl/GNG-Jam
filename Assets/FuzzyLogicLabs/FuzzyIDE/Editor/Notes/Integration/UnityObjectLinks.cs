using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>
    /// Links between notes and Unity objects: scripts → [[File.cs]], assets → [[asset:GUID|Name]] (survives moves and
    /// renames), scene objects → [[go:GlobalObjectId|Name]].
    /// </summary>
    public static class UnityObjectLinks
    {
        /// <summary>Wiki link (with brackets) for a project asset or scene object, or null.</summary>
        public static string CreateLink(UnityEngine.Object obj)
        {
            if (obj == null) return null;

            string assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath))
            {
                if (obj is MonoScript || NoteRefLinks.Parse(assetPath)?.Kind == NoteRefKind.Code)
                    return $"[[{Path.GetFileName(assetPath)}]]";
                if (AssetDatabase.IsValidFolder(assetPath)) return $"[[asset:{assetPath}|{Escape(obj.name)}]]";
                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                return string.IsNullOrEmpty(guid) ? $"[[asset:{assetPath}]]" : $"[[asset:{guid}|{Escape(obj.name)}]]";
            }

            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go != null && go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path)) // unsaved scenes have no stable id
            {
                var id = GlobalObjectId.GetGlobalObjectIdSlow(go);
                if (id.identifierType != 0) return $"[[go:{id}|{Escape(go.name)}]]";
            }
            return null;
        }

        /// <summary>Index keys under which notes may reference <paramref name="obj"/> (see <see cref="NoteIndex.GetRefs"/>).</summary>
        public static List<string> GetKeys(UnityEngine.Object obj)
        {
            var keys = new List<string>();
            if (obj == null) return keys;

            string assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath))
            {
                keys.Add(NoteRefLinks.PathKey(assetPath));
                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid)) keys.Add(NoteRefLinks.GuidKey(guid));
                return keys;
            }

            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go != null && go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path)) // unsaved scenes have no stable id
            {
                var id = GlobalObjectId.GetGlobalObjectIdSlow(go);
                if (id.identifierType != 0) keys.Add(NoteRefLinks.GameObjectKey(id.ToString()));
            }
            return keys;
        }

        /// <summary>Notes that reference <paramref name="obj"/>; code files also match [[File.cs]] links.</summary>
        public static List<NoteRefHit> FindReferences(UnityEngine.Object obj)
        {
            var result = new List<NoteRefHit>();
            if (obj == null) return result;
            var store = NoteStore.Instance;
            store.EnsureLoaded();

            foreach (var key in GetKeys(obj)) result.AddRange(store.Index.GetRefs(key));
            string assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath) && NoteRefLinks.Parse(assetPath)?.Kind == NoteRefKind.Code)
            {
                foreach (var hit in store.Index.GetCodeRefs(assetPath))
                    if (!result.Contains(hit)) result.Add(hit);
            }
            return result;
        }

        /// <summary>Selects and pings a scene object; offers to open its scene when it is not loaded.</summary>
        public static bool OpenGameObject(string globalObjectId)
        {
            if (!GlobalObjectId.TryParse(globalObjectId, out var id))
            {
                Debug.LogWarning($"[FuzzyNotes] Not a valid scene object link: {globalObjectId}");
                return false;
            }

            var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
            if (obj == null)
            {
                string scenePath = AssetDatabase.GUIDToAssetPath(id.assetGUID.ToString());
                if (string.IsNullOrEmpty(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning("[FuzzyNotes] The linked object no longer exists.");
                    return false;
                }
                if (EditorApplication.isPlaying ||
                    !EditorUtility.DisplayDialog("Open Scene", $"The linked object is in '{Path.GetFileNameWithoutExtension(scenePath)}', which is not open. Open it?", "Open Scene", "Cancel") ||
                    !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return false;
                }
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
                if (obj == null)
                {
                    Debug.LogWarning("[FuzzyNotes] The linked object was not found in its scene; it may have been deleted.");
                    return false;
                }
            }

            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
            if (obj is GameObject && SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
            return true;
        }

        /// <summary>Asks for a target note and appends links to the given objects.</summary>
        public static void AddObjectsToNote(IList<UnityEngine.Object> objects)
        {
            var links = new List<string>();
            foreach (var o in objects)
            {
                string link = CreateLink(o);
                if (link != null) links.Add(link);
            }
            if (links.Count == 0)
            {
                EditorUtility.DisplayDialog("Fuzzy Notes", "These objects can't be linked yet. Objects in an unsaved scene have no stable id — save the scene first.", "OK");
                return;
            }

            string title = objects.Count == 1 ? objects[0].name : $"{objects.Count} objects";
            string markdown = "\n" + string.Join("\n", links.ConvertAll(l => "- " + l)) + "\n";
            NoteTargetPicker.Pick($"Link {title} in note…", objects.Count == 1 ? $"Notes on {objects[0].name}" : null,
                note => CodeNotes.AppendAndReveal(note, markdown, links[links.Count - 1]));
        }

        private static string Escape(string name) => (name ?? "").Replace("|", "/").Replace("]", ")").Replace("[", "(");

        // ── Menus ────────────────────────────────────────────────────────────

        [MenuItem("Assets/Fuzzy Notes/Link in Note…", false, 2000)]
        private static void LinkSelectedAssets() => AddObjectsToNote(Selection.objects);

        [MenuItem("Assets/Fuzzy Notes/Link in Note…", true)]
        private static bool LinkSelectedAssetsValidate() => Selection.objects != null && Selection.objects.Length > 0;

        [MenuItem("Assets/Fuzzy Notes/Show Notes", false, 2001)]
        private static void ShowNotesForAsset() => NoteInspectorBadge.ShowNotesFor(Selection.activeObject);

        private static double s_LastHierarchyInvoke;

        [MenuItem("GameObject/Fuzzy Notes/Link in Note…", false, 49)]
        private static void LinkSelectedGameObjects(MenuCommand command)
        {
            // Hierarchy context menus run once per selected object; handle the whole selection once.
            double now = EditorApplication.timeSinceStartup;
            if (now - s_LastHierarchyInvoke < 0.3) return;
            s_LastHierarchyInvoke = now;
            if (Selection.gameObjects.Length > 0) AddObjectsToNote(Selection.gameObjects);
        }

        [MenuItem("GameObject/Fuzzy Notes/Show Notes", false, 50)]
        private static void ShowNotesForGameObject() => NoteInspectorBadge.ShowNotesFor(Selection.activeGameObject);
    }
}
