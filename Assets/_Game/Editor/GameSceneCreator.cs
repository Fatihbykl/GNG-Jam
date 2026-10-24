using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace KatKat.Editor
{
    /// <summary>
    /// Menü: Kat Kat Yıkım → Oyun Sahnesini Oluştur.
    /// Kamera, ışık ve GameManager içeren sahneyi kurar, ip tiplerini asset olarak oluşturur
    /// ve Tensio'nun patlama efektini bağlar.
    /// </summary>
    public static class GameSceneCreator
    {
        private const string ScenePath = "Assets/_Game/Scenes/Game.unity";
        private const string DataFolder = "Assets/_Game/Data";
        private const string VfxPath = "Assets/FuzzyLogicLab/Tensio/Demos/Prefabs/VFX/ExplosionParticle.prefab";

        [MenuItem("Kat Kat Yıkım/Oyun Sahnesini Oluştur")]
        public static void CreateGameScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<OrbitCamera>();

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            var gm = new GameObject("Game").AddComponent<GameManager>();
            gm.RopeTypes = EnsureRopeTypeAssets();
            gm.Content = ContentAssetCreator.EnsureContent();
            gm.ExplosionVfxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(VfxPath);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);
            Selection.activeObject = gm.gameObject;
            Debug.Log($"[KatKat] Sahne oluşturuldu: {ScenePath}. Play'e basarak deneyebilirsin.");
        }

        private static RopeTypeSO[] EnsureRopeTypeAssets()
        {
            if (!AssetDatabase.IsValidFolder(DataFolder)) AssetDatabase.CreateFolder("Assets/_Game", "Data");

            var defaults = RopeTypeSO.CreateDefaults();
            string[] fileNames = { "Rope_Thin", "Rope_Steel", "Rope_Elastic" };
            var result = new RopeTypeSO[defaults.Length];

            for (int i = 0; i < defaults.Length; i++)
            {
                string path = $"{DataFolder}/{fileNames[i]}.asset";
                var existing = AssetDatabase.LoadAssetAtPath<RopeTypeSO>(path);
                if (existing != null)
                {
                    result[i] = existing;
                    Object.DestroyImmediate(defaults[i]);
                    continue;
                }
                AssetDatabase.CreateAsset(defaults[i], path);
                result[i] = defaults[i];
            }

            AssetDatabase.SaveAssets();
            return result;
        }

        private static void AddToBuildSettings(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == path);
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
