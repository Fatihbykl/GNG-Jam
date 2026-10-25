using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KatKat.Editor
{
    /// <summary>
    /// Menü: Kat Kat Yıkım → Modelleri Bağla.
    /// Assets/Models içindeki FBX'leri oyuna bağlar: kolon (sağlam + kırık tuğlalar), tek tuğlanın kırık versiyonları
    /// ve odadaki eşyalar. Her model için kökü boş bir prefab sarmalayıcısı kurar (FBX kökündeki Blender dönüşü
    /// yuvaya sığdırmayı bozmasın diye) ve GameContent'e atar. Var olan sarmalayıcılara ve eşya ayarlarına dokunmaz.
    /// </summary>
    public static class ModelContentLinker
    {
        private const string ModelFolder = "Assets/Models";
        private const string StructureFolder = "Assets/_Game/Prefabs/Structure";
        private const string ItemFolder = "Assets/_Game/Prefabs/Items";
        private const string ItemDataFolder = "Assets/_Game/Data/Items";
        private const string DefaultColumn = StructureFolder + "/Column.prefab";
        private const string DefaultBearingColumn = StructureFolder + "/BearingColumn.prefab";

        private struct ItemSpec
        {
            public string Model, Name;
            public ItemSize Size;
            public float Mass, Weight;

            public ItemSpec(string model, string name, ItemSize size, float mass, float weight = 1f)
            {
                Model = model;
                Name = name;
                Size = size;
                Mass = mass;
                Weight = weight;
            }
        }

        private static readonly ItemSpec[] Items =
        {
            new ItemSpec("Armchair", "Koltuk", ItemSize.Big, 35f),
            new ItemSpec("Armchair_2", "Kanepe", ItemSize.Big, 60f, 0.7f),
            new ItemSpec("Armchair_2_mini", "İkili koltuk", ItemSize.Big, 45f),
            new ItemSpec("washingan_machine", "Çamaşır makinesi", ItemSize.Big, 70f),
            new ItemSpec("TV", "Televizyon", ItemSize.Small, 12f),
            new ItemSpec("carpet", "Halı", ItemSize.Small, 10f),
            new ItemSpec("Lamp", "Lamba", ItemSize.Small, 5f),
        };

        private static readonly string[] BrokenBricks = { "Brick_Broken_A", "Brick_Broken_B", "Brick_Broken_C" };

        [MenuItem("Kat Kat Yıkım/Modelleri Bağla")]
        public static void Link()
        {
            var content = ContentAssetCreator.EnsureContent();
            var log = new List<string>();

            // Fizik parçalarına çalışma anında MeshCollider takılıyor: build'de mesh okunabilir olmalı.
            MakeReadable("Kolon");
            foreach (var b in BrokenBricks) MakeReadable(b);

            var column = Wrap("Kolon", $"{StructureFolder}/Column_Kolon.prefab");
            if (column != null)
            {
                content.ColumnPrefab = column;
                // Ayrı taşıyıcı modeli yok: aynı kolon koyu renge boyanır.
                if (content.BearingColumnPrefab != null && AssetDatabase.GetAssetPath(content.BearingColumnPrefab) == DefaultBearingColumn)
                    content.BearingColumnPrefab = null;
                log.Add("Kolon");
            }

            foreach (var b in BrokenBricks)
            {
                var fbx = LoadModel(b);
                if (fbx == null || content.BrokenBrickPrefabs.Contains(fbx)) continue;
                content.BrokenBrickPrefabs.Add(fbx);
                log.Add(b);
            }

            foreach (var spec in Items)
            {
                var prefab = Wrap(spec.Model, $"{ItemFolder}/Item_{AssetName(spec.Model)}.prefab");
                if (prefab == null) continue;
                var def = content.Items.Find(d => d != null && d.DisplayName == spec.Name);
                if (def == null)
                {
                    string path = $"{ItemDataFolder}/Item_{AssetName(spec.Name)}.asset";
                    def = AssetDatabase.LoadAssetAtPath<ItemDefinitionSO>(path);
                    if (def == null)
                    {
                        def = ItemDefinitionSO.Create(spec.Name, spec.Size, spec.Mass, Vector3.one * 0.5f, Color.white);
                        def.SpawnWeight = spec.Weight;
                        AssetDatabase.CreateAsset(def, path);
                    }
                    content.Items.Add(def);
                }
                if (def.Prefab == prefab) continue;
                // Sadece prototip küpünü (ya da boşu) değiştir; elle atanmış bir modeli ezme.
                if (def.Prefab != null && !AssetDatabase.GetAssetPath(def.Prefab).StartsWith(ItemFolder + "/Item_" + AssetName(def.DisplayName)))
                    continue;
                def.Prefab = prefab;
                EditorUtility.SetDirty(def);
                log.Add(spec.Name);
            }

            EditorUtility.SetDirty(content);
            AssetDatabase.SaveAssets();
            Selection.activeObject = content;
            Debug.Log(log.Count > 0
                ? $"[KatKat] Modeller bağlandı: {string.Join(", ", log)}"
                : "[KatKat] Bağlanacak yeni model yok; hepsi zaten atanmış.");
        }

        private static GameObject LoadModel(string name)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelFolder}/{name}.fbx");
            if (go == null) Debug.LogWarning($"[KatKat] Model bulunamadı: {ModelFolder}/{name}.fbx");
            return go;
        }

        /// <summary>Boş kök + "Model" adlı FBX örneği. Pivot ve dönüş önemsiz: oyun modelin sınırlarını ölçer.</summary>
        private static GameObject Wrap(string model, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            var fbx = LoadModel(model);
            if (fbx == null) return null;

            var root = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            instance.name = "Model";
            instance.transform.SetParent(root.transform, false);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static void MakeReadable(string model)
        {
            if (AssetImporter.GetAtPath($"{ModelFolder}/{model}.fbx") is ModelImporter importer && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }

        private static string AssetName(string name)
        {
            return name.Replace('ı', 'i').Replace('ö', 'o').Replace('ü', 'u').Replace('ş', 's').Replace('ç', 'c')
                .Replace('ğ', 'g').Replace('İ', 'I').Replace('Ç', 'C').Replace(' ', '_');
        }
    }
}
