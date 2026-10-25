using UnityEditor;
using UnityEngine;

namespace KatKat.Editor
{
    /// <summary>
    /// Menü: Kat Kat Yıkım → Patlama Efektini Prefab Yap.
    /// Kodla kurulan şarj patlamasını (<see cref="Vfx"/>: flaş, ateş, kıvılcım, beton parçaları, duman) aynı ayarlarla
    /// prefab olarak kaydeder; malzemeler ve yumuşak daire dokusu yanına asset olarak yazılır. Oyundaki efekt değişmez.
    /// Yeniden çalıştırınca var olan asset'lerin içeriği güncellenir, referanslar kopmaz.
    /// </summary>
    public static class BlastPrefabCreator
    {
        private const string PrefabFolder = "Assets/_Game/Prefabs/Vfx";
        private const string MaterialFolder = "Assets/_Game/Materials/Vfx";
        private const string ContentPath = "Assets/_Game/Data/GameContent.asset";

        [MenuItem("Kat Kat Yıkım/Patlama Efektini Prefab Yap")]
        public static void Create()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[KatKat] Patlama prefabı Play modundan çıkınca oluşturulabilir.");
                return;
            }

            EnsureFolder(PrefabFolder);
            EnsureFolder(MaterialFolder);

            var content = AssetDatabase.LoadAssetAtPath<GameContentSO>(ContentPath);
            var root = Vfx.BuildBlastStandalone(content, out var alpha, out var additive, out var debris, out var soft);
            try
            {
                // Önce doku, sonra onu kullanan malzemeler kaydedilir
                var softAsset = Save(soft, $"{MaterialFolder}/Vfx_Soft.asset");
                alpha.SetTexture("_BaseMap", softAsset);
                additive.SetTexture("_BaseMap", softAsset);
                var alphaAsset = Save(alpha, $"{MaterialFolder}/Vfx_Alpha.mat");
                var additiveAsset = Save(additive, $"{MaterialFolder}/Vfx_Additive.mat");
                var debrisAsset = Save(debris, $"{MaterialFolder}/Vfx_Debris.mat");

                foreach (var r in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (m == alpha) r.sharedMaterial = alphaAsset;
                    else if (m == additive) r.sharedMaterial = additiveAsset;
                    else if (m == debris) r.sharedMaterial = debrisAsset;
                }

                string path = $"{PrefabFolder}/BlastFx.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
                Debug.Log($"[KatKat] Patlama efekti prefabı hazır: {path}");
            }
            finally
            {
                Object.DestroyImmediate(root);
                // Var olan asset'e kopyalanan geçici nesneler artık gereksiz
                foreach (var o in new Object[] { soft, alpha, additive, debris })
                {
                    if (o != null && !AssetDatabase.Contains(o)) Object.DestroyImmediate(o);
                }
            }
        }

        /// <summary>
        /// Yeni asset oluşturur; varsa içeriğini kopyalar ki GUID ve prefab referansları korunsun.
        /// Kopyalanan geçici nesneyi yok etmez: renderer eşleştirmesi bitince çağıran temizler.
        /// </summary>
        private static T Save<T>(T obj, string path) where T : Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(obj, path);
                return obj;
            }
            string name = existing.name;
            EditorUtility.CopySerialized(obj, existing);
            existing.name = name;
            EditorUtility.SetDirty(existing);
            return existing;
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
