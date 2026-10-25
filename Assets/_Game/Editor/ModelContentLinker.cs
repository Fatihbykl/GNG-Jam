using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KatKat.Editor
{
    /// <summary>
    /// Menü: Kat Kat Yıkım → Modelleri Bağla.
    /// Assets/Models içindeki FBX'leri oyuna bağlar: kolon (sağlam + kırık tuğlalar), tek tuğlanın kırık versiyonları,
    /// tavan/zemin (Ground), duvar (Wall_A), ip kancaları (hook), şarj (Dynamite_set) ve odadaki eşyalar. Her model için kökü boş bir prefab sarmalayıcısı kurar (FBX kökündeki Blender dönüşü
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
            new ItemSpec("bookshelf", "Kitaplık", ItemSize.Big, 40f),
            new ItemSpec("box1", "Koli", ItemSize.Small, 8f),
            new ItemSpec("box2", "Büyük koli", ItemSize.Big, 25f, 0.7f),
        };

        /// <summary>Kodun prototip küplerden kurduğu varsayılan yapı prefabları; yalnızca bunların yerine model konur.</summary>
        private static bool IsDefaultStructure(Object current, string name)
        {
            return current == null || AssetDatabase.GetAssetPath(current) == $"{StructureFolder}/{name}.prefab";
        }

        private static readonly string[] BrokenBricks = { "Brick_Broken_A", "Brick_Broken_B", "Brick_Broken_C" };

        [MenuItem("Kat Kat Yıkım/Modelleri Bağla")]
        public static void Link()
        {
            var content = ContentAssetCreator.EnsureContent();
            var log = new List<string>();

            // Fizik parçalarına çalışma anında MeshCollider takılıyor: build'de mesh okunabilir olmalı.
            MakeReadable("Kolon");
            foreach (var b in BrokenBricks) MakeReadable(b);
            MakeReadable("Ground");
            MakeReadable("Wall_A");

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

            LinkStructure(content, log);

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

        /// <summary>Tavan/zemin, duvar, kancalar ve şarj modeli.</summary>
        private static void LinkStructure(GameContentSO content, List<string> log)
        {
            // Ground_Intact + Ground_Fractured: aynı model hem tavan hem alt katın zemini; aktif katın tavanı patlamada kırılır.
            var ground = Wrap("Ground", $"{StructureFolder}/Ceiling_Ground.prefab");
            if (ground != null && IsDefaultStructure(content.CeilingPrefab, "Ceiling"))
            {
                content.CeilingPrefab = ground;
                log.Add("Ground");
            }

            var wall = Wrap("Wall_A", $"{StructureFolder}/Wall_A.prefab");
            if (wall != null && IsDefaultStructure(content.WallPrefab, "Wall"))
            {
                content.WallPrefab = wall;
                // Prototip çeşitler (ör. pencereli küp duvar) Wall_A'nın yerini almasın
                content.WallVariants.RemoveAll(v => v == null || v.Prefab == null || IsDefaultStructure(v.Prefab, "Wall_Window"));
                log.Add("Wall_A");
            }

            var columnHook = Wrap("hook", $"{StructureFolder}/Hook_Column.prefab", ArrangeColumnHook);
            if (columnHook != null && IsDefaultStructure(content.ColumnAnchorPrefab, "ColumnAnchor"))
            {
                content.ColumnAnchorPrefab = columnHook;
                log.Add("hook (kolon)");
            }

            var floorHook = Wrap("hook", $"{StructureFolder}/Hook_Floor.prefab", ArrangeFloorHook);
            if (floorHook != null && IsDefaultStructure(content.FloorRingPrefab, "FloorRing"))
            {
                content.FloorRingPrefab = floorHook;
                log.Add("hook (zemin)");
            }

            var dynamite = Wrap("Dynamite_set", $"{StructureFolder}/Charge_Dynamite.prefab", ArrangeDynamite);
            if (dynamite != null && IsDefaultStructure(content.ChargeMarkerPrefab, "ChargeMarker"))
            {
                content.ChargeMarkerPrefab = dynamite;
                log.Add("Dynamite_set");
            }
        }

        /// <summary>
        /// Kanca modeli yatay bir halka ve halkanın bir yanında küçük bir pimden oluşur. Köşe noktalarının çoğu
        /// halkada olduğu için ortalamaları halkaya yakındır; pim sınırların öbür ucundadır.
        /// </summary>
        private static float HookStudSide(Transform root, Bounds b) => Mathf.Sign(b.center.x - MeanVertex(root).x);

        /// <summary>Kolon kancası: pim kolona (−Z) gömülür, halka kat merkezine (+Z) uzanır; halka merkezi pivotta.</summary>
        private static void ArrangeColumnHook(Transform root, Transform model)
        {
            float studSide = HookStudSide(root, PrefabPlacement.MeasureLocal(root));
            model.rotation = Quaternion.Euler(0f, studSide > 0f ? 90f : -90f, 0f) * model.rotation;
            var b = PrefabPlacement.MeasureLocal(root);
            float diameter = b.size.x;
            model.position -= new Vector3(b.center.x, b.center.y, b.max.z - diameter * 0.5f);
        }

        /// <summary>Zemin kancası: halka yere yatık; ankraj zeminden 8 cm yukarıda olduğu için halkanın altı −0.08'de.</summary>
        private static void ArrangeFloorHook(Transform root, Transform model)
        {
            var b = PrefabPlacement.MeasureLocal(root);
            float studSide = HookStudSide(root, b);
            float diameter = b.size.z;
            float x = studSide > 0f ? b.min.x + diameter * 0.5f : b.max.x - diameter * 0.5f;
            model.position -= new Vector3(x, b.min.y + 0.08f, b.center.z);
        }

        /// <summary>Dinamit seti kolon yüzüne yapışır: arkası pivotta, ön yüzü +Z'ye bakar.</summary>
        private static void ArrangeDynamite(Transform root, Transform model)
        {
            var b = PrefabPlacement.MeasureLocal(root);
            model.position -= new Vector3(b.center.x, b.center.y, b.min.z);
        }

        private static Vector3 MeanVertex(Transform root)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices)
                {
                    sum += m.MultiplyPoint3x4(v);
                    n++;
                }
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        private static GameObject LoadModel(string name)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelFolder}/{name}.fbx");
            if (go == null) Debug.LogWarning($"[KatKat] Model bulunamadı: {ModelFolder}/{name}.fbx");
            return go;
        }

        /// <summary>
        /// Boş kök + "Model" adlı FBX örneği. Pivot ve dönüş önemsiz: oyun modelin sınırlarını ölçer.
        /// Ölçülmeden pivotuyla yerleşen görsel işaretler için <paramref name="arrange"/> modeli kökün içinde konumlar.
        /// </summary>
        private static GameObject Wrap(string model, string path, System.Action<Transform, Transform> arrange = null)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;
            var fbx = LoadModel(model);
            if (fbx == null) return null;

            var root = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            instance.name = "Model";
            instance.transform.SetParent(root.transform, false);
            arrange?.Invoke(root.transform, instance.transform);
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
