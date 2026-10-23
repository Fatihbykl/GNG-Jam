using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core;
using FuzzyLogicLabs.FuzzyIDE.Editor.Notes.UI;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Integration
{
    /// <summary>
    /// "📝 N notes" button in the Inspector header of assets and scene objects that notes link to.
    /// Counts are cached per object and refreshed when the note index changes.
    /// </summary>
    [InitializeOnLoad]
    public static class NoteInspectorBadge
    {
        private static readonly Dictionary<Object, int> s_CountCache = new Dictionary<Object, int>();
        private static int s_CacheVersion = -1;
        private static GUIStyle s_Style;

        static NoteInspectorBadge()
        {
            UnityEditor.Editor.finishedDefaultHeaderGUI -= OnHeaderGUI;
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnHeaderGUI;
        }

        private static void OnHeaderGUI(UnityEditor.Editor editor)
        {
            if (editor == null || editor.targets.Length != 1) return;
            var target = editor.target;
            if (target == null) return;

            // Importer inspectors (textures, models…) target the importer; the notes link the asset itself.
            if (target is AssetImporter importer) target = AssetDatabase.LoadMainAssetAtPath(importer.assetPath);
            if (target == null) return;

            int count = GetCount(target);
            if (count == 0) return;

            if (s_Style == null)
            {
                s_Style = new GUIStyle(EditorStyles.miniButton) { fontSize = 10, fixedHeight = 18 };
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                var label = new GUIContent(count == 1 ? "📝 1 note" : $"📝 {count} notes", "Fuzzy Notes linking to this object");
                if (GUILayout.Button(label, s_Style, GUILayout.ExpandWidth(false))) ShowNotesFor(target);
            }
        }

        private static int GetCount(Object target)
        {
            // Don't load the vault just to draw an Inspector; notes are loaded by the IDE or Notes window.
            var store = NoteStore.Instance;
            if (store.Notes.Count == 0) return 0;

            if (s_CacheVersion != store.Index.Version)
            {
                s_CountCache.Clear();
                s_CacheVersion = store.Index.Version;
            }

            // Keyed by the object itself: Object equality is identity-based on every Unity version,
            // unlike GetInstanceID (obsolete in 6.6) / GetEntityId (missing before 6.x).
            if (!s_CountCache.TryGetValue(target, out int count))
            {
                var notes = new HashSet<NoteItem>();
                foreach (var hit in UnityObjectLinks.FindReferences(target)) notes.Add(hit.Note);
                count = notes.Count;
                s_CountCache[target] = count;
            }
            return count;
        }

        /// <summary>Opens the notes that link to <paramref name="target"/>, or offers to link it when there are none.</summary>
        public static void ShowNotesFor(Object target)
        {
            if (target == null) return;
            var hits = UnityObjectLinks.FindReferences(target);
            if (hits.Count == 0)
            {
                if (EditorUtility.DisplayDialog("Fuzzy Notes", $"No notes link to '{target.name}' yet. Link it in a note now?", "Link in Note…", "Cancel"))
                    UnityObjectLinks.AddObjectsToNote(new[] { target });
                return;
            }
            CodeNotes.OpenHits(hits, $"Notes about {target.name}");
        }
    }
}
