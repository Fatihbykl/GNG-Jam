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
            gm.Rope = EnsureRopeAsset();
            gm.Content = ContentAssetCreator.EnsureContent();
            gm.ExplosionVfxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(VfxPath);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);
            Selection.activeObject = gm.gameObject;
            Debug.Log($"[KatKat] Sahne oluşturuldu: {ScenePath}. Play'e basarak deneyebilirsin.");
        }

        /// <summary>Oyundaki tek ip türü: Data/Rope.asset (yoksa varsayılan değerlerle oluşturulur).</summary>
        private static RopeTypeSO EnsureRopeAsset()
        {
            if (!AssetDatabase.IsValidFolder(DataFolder)) AssetDatabase.CreateFolder("Assets/_Game", "Data");

            string path = $"{DataFolder}/Rope.asset";
            var existing = AssetDatabase.LoadAssetAtPath<RopeTypeSO>(path);
            if (existing != null) return existing;

            var rope = RopeTypeSO.CreateDefault();
            AssetDatabase.CreateAsset(rope, path);
            AssetDatabase.SaveAssets();
            return rope;
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
