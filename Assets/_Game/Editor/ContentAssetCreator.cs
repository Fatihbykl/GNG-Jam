using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace KatKat.Editor
{
    /// <summary>
    /// Menü: Kat Kat Yıkım → İçerik ve Örnek Prefabları Oluştur.
    /// Her yapı parçası ve eşya için küp modelli örnek prefab, eşya tanımları ve GameContent asset'i üretir.
    /// Prefabların içindeki "Model" objesini kendi modelinle değiştirmen yeterli.
    /// Var olan dosyaların üzerine yazmaz, yani modellerin güvende.
    /// </summary>
    public static class ContentAssetCreator
    {
        private const string Root = "Assets/_Game";
        private const string StructureFolder = Root + "/Prefabs/Structure";
        private const string ItemFolder = Root + "/Prefabs/Items";
        private const string MaterialFolder = Root + "/Materials";
        private const string ItemDataFolder = Root + "/Data/Items";
        private const string ContentPath = Root + "/Data/GameContent.asset";

        [MenuItem("Kat Kat Yıkım/İçerik ve Örnek Prefabları Oluştur")]
        public static void CreateFromMenu()
        {
            var content = EnsureContent();

            var gm = Object.FindAnyObjectByType<GameManager>();
            if (gm != null && gm.Content != content)
            {
                Undo.RecordObject(gm, "Assign Game Content");
                gm.Content = content;
                EditorSceneManager.MarkSceneDirty(gm.gameObject.scene);
            }

            Selection.activeObject = content;
            EditorGUIUtility.PingObject(content);
            Debug.Log($"[KatKat] İçerik hazır: {ContentPath}. Prefablar: {StructureFolder}, {ItemFolder}");
        }

        public static GameContentSO EnsureContent()
        {
            foreach (var folder in new[] { StructureFolder, ItemFolder, MaterialFolder, ItemDataFolder }) EnsureFolder(folder);

            var cfg = new GameConfig();
            float h = cfg.floorHeight - cfg.slabThickness;

            var content = AssetDatabase.LoadAssetAtPath<GameContentSO>(ContentPath);
            bool created = content == null;
            if (created) content = ScriptableObject.CreateInstance<GameContentSO>();

            content.ColumnPrefab = Keep(content.ColumnPrefab,
                () => StructurePrefab("Column", new Vector3(cfg.columnSize, h, cfg.columnSize), GameMaterials.ColumnColor));
            content.BearingColumnPrefab = Keep(content.BearingColumnPrefab,
                () => StructurePrefab("BearingColumn", new Vector3(cfg.columnSize, h, cfg.columnSize), GameMaterials.BearingColumnColor));
            content.WallPrefab = Keep(content.WallPrefab,
                () => StructurePrefab("Wall", new Vector3(cfg.cellSize - cfg.columnSize, h, cfg.wallThickness), GameMaterials.WallColor));
            content.CeilingPrefab = Keep(content.CeilingPrefab,
                () => StructurePrefab("Ceiling", new Vector3(cfg.cellSize, cfg.slabThickness, cfg.cellSize), GameMaterials.Concrete));
            content.RubblePrefab = Keep(content.RubblePrefab,
                () => StructurePrefab("Rubble", new Vector3(0.36f, 1.2f, 0.36f), GameMaterials.ColumnColor));
            content.ColumnAnchorPrefab = Keep(content.ColumnAnchorPrefab,
                () => MarkerPrefab("ColumnAnchor", PrimitiveType.Sphere, Vector3.one * 0.22f, GameMaterials.AnchorColor));
            content.FloorRingPrefab = Keep(content.FloorRingPrefab,
                () => MarkerPrefab("FloorRing", PrimitiveType.Cylinder, new Vector3(0.3f, 0.04f, 0.3f), GameMaterials.AnchorColor));
            content.ChargeMarkerPrefab = Keep(content.ChargeMarkerPrefab,
                () => MarkerPrefab("ChargeMarker", PrimitiveType.Cube, new Vector3(0.5f, 0.35f, 0.5f), GameMaterials.ChargeColor));

            if (content.Items == null) content.Items = new List<ItemDefinitionSO>();
            if (content.Items.Count == 0)
            {
                foreach (var def in GameContentSO.CreateDefaultItems()) content.Items.Add(EnsureItem(def));
            }

            if (created) AssetDatabase.CreateAsset(content, ContentPath);
            EditorUtility.SetDirty(content);
            AssetDatabase.SaveAssets();
            return content;
        }

        private static GameObject Keep(GameObject current, System.Func<GameObject> create) => current != null ? current : create();

        /// <summary>Kök boş obje + ortalanmış "Model" küpü, gerçek ölçüde. Collider yok: kod modelin sınırlarından ekler.</summary>
        private static GameObject StructurePrefab(string name, Vector3 size, Color color)
        {
            string path = $"{StructureFolder}/{name}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var root = new GameObject(name);
            AddModel(root.transform, "Model", PrimitiveType.Cube, Vector3.zero, size, color);
            return Save(root, path);
        }

        /// <summary>Sadece görsel işaret; pivot bağlantı noktasıdır.</summary>
        private static GameObject MarkerPrefab(string name, PrimitiveType shape, Vector3 size, Color color)
        {
            string path = $"{StructureFolder}/{name}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var root = new GameObject(name);
            AddModel(root.transform, "Model", shape, Vector3.zero, size, color);
            return Save(root, path);
        }

        private static ItemDefinitionSO EnsureItem(ItemDefinitionSO def)
        {
            string fileName = AssetName(def.DisplayName);
            string defPath = $"{ItemDataFolder}/{fileName}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<ItemDefinitionSO>(defPath);
            if (existing != null)
            {
                Object.DestroyImmediate(def);
                return existing;
            }

            def.Prefab = ItemPrefab(fileName, def);
            AssetDatabase.CreateAsset(def, defPath);
            return def;
        }

        /// <summary>
        /// Eşya prefabı: pivot tabanda. Gevşek parçalar (ör. kitaplar) kendi Rigidbody'si olan alt objelerdir.
        /// </summary>
        private static GameObject ItemPrefab(string fileName, ItemDefinitionSO def)
        {
            string path = $"{ItemFolder}/{fileName}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var size = def.FallbackSize;
            var root = new GameObject(def.DisplayName);
            AddModel(root.transform, "Model", PrimitiveType.Cube, Vector3.up * (size.y * 0.5f), size, def.FallbackColor);

            Color[] partColors = { new Color(0.8f, 0.2f, 0.25f), new Color(0.2f, 0.45f, 0.8f), new Color(0.3f, 0.7f, 0.35f) };
            float spacing = def.FallbackLooseParts > 0 ? size.x / def.FallbackLooseParts : 0f;
            for (int i = 0; i < def.FallbackLooseParts; i++)
            {
                var part = new GameObject($"LoosePart_{i + 1}");
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = new Vector3((i - (def.FallbackLooseParts - 1) * 0.5f) * spacing, size.y + 0.16f, 0f);
                var rb = part.AddComponent<Rigidbody>();
                rb.mass = def.LoosePartMass;
                AddModel(part.transform, "Model", PrimitiveType.Cube, Vector3.zero,
                    new Vector3(Mathf.Min(0.22f, spacing * 0.8f), 0.3f, Mathf.Min(0.28f, size.z * 0.9f)),
                    partColors[i % partColors.Length]);
            }

            return Save(root, path);
        }

        private static void AddModel(Transform parent, string name, PrimitiveType shape, Vector3 localPos, Vector3 size,
            Color color)
        {
            var model = GameObject.CreatePrimitive(shape);
            model.name = name;
            Object.DestroyImmediate(model.GetComponent<Collider>());
            model.transform.SetParent(parent, false);
            model.transform.localPosition = localPos;
            model.transform.localScale = size;
            model.GetComponent<Renderer>().sharedMaterial = EnsureMaterial(color);
        }

        private static Material EnsureMaterial(Color color)
        {
            string path = $"{MaterialFolder}/M_{ColorUtility.ToHtmlStringRGB(color)}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static GameObject Save(GameObject root, string path)
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static string AssetName(string displayName)
        {
            return "Item_" + displayName.Replace('ı', 'i').Replace('ö', 'o').Replace('ü', 'u').Replace('ş', 's')
                .Replace('ç', 'c').Replace('ğ', 'g').Replace('İ', 'I').Replace(' ', '_');
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
    }
}
