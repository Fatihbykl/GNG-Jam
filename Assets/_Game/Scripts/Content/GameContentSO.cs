using System;
using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Oyunun tüm model içeriği tek yerde: yapı parçalarının ve eşyaların prefabları.
    /// Boş bırakılan her alan için prototip küpü kullanılır, yani modeller teker teker eklenebilir.
    /// </summary>
    /// <summary>Ağırlıklı rastgele seçilen prefab (ör. duvar çeşitleri).</summary>
    [Serializable]
    public class WeightedPrefab
    {
        public GameObject Prefab;
        [Min(0f)] public float Weight = 1f;
    }

    [CreateAssetMenu(menuName = "Kat Kat Yıkım/Game Content", fileName = "GameContent")]
    public class GameContentSO : ScriptableObject
    {
        [Header("Yapı parçaları")]
        [Tooltip("Şarjlı kolon. Yuva: kolon kalınlığı × (kat yüksekliği − döşeme) × kolon kalınlığı.")]
        public GameObject ColumnPrefab;
        [Tooltip("Taşıyıcı (patlamayan) kolon. Boşsa Column prefabı kullanılır ve koyu renge boyanır.")]
        public GameObject BearingColumnPrefab;
        [Tooltip("Dış duvar. Yerel X = duvar boyu, Y = yükseklik, +Z = binanın dışına bakan yüz.\n" +
                 "Cell Fracture modeli: kökün altında 2+ parça mesh varsa kırılabilir olur.")]
        public GameObject WallPrefab;
        [Tooltip("Düz duvarın (WallPrefab) çeşitler arasındaki ağırlığı.")]
        [Min(0f)] public float WallPrefabWeight = 2f;
        [Tooltip("Dış duvar çeşitleri: pencereli, kapılı, balkonlu... Aynı yuvaya ve eksen kurallarına uyar.\n" +
                 "Her duvar için WallPrefab ile birlikte ağırlığa göre seçilir. Cell Fracture modelleri de olabilir.")]
        public List<WeightedPrefab> WallVariants = new List<WeightedPrefab>();
        [Tooltip("Tavan döşemesi (bir hücre), aynı zamanda alt katın zemini. Yuva: hücre × döşeme kalınlığı × hücre.\n" +
                 "Cell Fracture modeli: kökün altında 2+ parça mesh varsa kırılabilir olur.")]
        public GameObject CeilingPrefab;
        [Tooltip("Kolon patlayınca kalan moloz parçası. Yuvaya sığdırılır.")]
        public GameObject RubblePrefab;
        [Tooltip("Tek tuğlanın Cell Fracture ile kırılmış versiyonları (ör. Brick_Broken_A/B/C, FBX'in kendisi).\n" +
                 "Sağlam/kırık çifti olan modelin (Kolon: *_Intact + *_Fractured) parçaları sert çarpmada bunlardan biriyle\n" +
                 "değişir; parçalar tuğlanın sınırlarına sığdırılır. Boşsa tuğlalar bölünmez.")]
        public List<GameObject> BrokenBrickPrefabs = new List<GameObject>();

        [Header("Görsel işaretler (collider'ları otomatik silinir)")]
        [Tooltip("Kolon ankrajı. Yerel +Z kat merkezine bakar.")]
        public GameObject ColumnAnchorPrefab;
        public GameObject FloorRingPrefab;
        public GameObject ChargeMarkerPrefab;

        [Header("Görünüm")]
        [Tooltip("Patlamada kamerayı kapatan tavan/duvarlar için saydam URP Lit şablonu (M_FadeTemplate).\n" +
                 "Rengi ve dokusu orijinal materyalden kopyalanır. Boşsa çalışma anında oluşturulur (build'de saydam varyant eksik kalabilir).")]
        public Material FadeMaterial;
        [Tooltip("Patlama, toz ve kıvılcım efektleri için URP Particles/Unlit şablonu (M_ParticleTemplate).")]
        public Material ParticleMaterial;
        [Tooltip("Cell Fracture kırık yüzleri için ortak malzeme. Modeldeki adı Interior/Inner/Ic/İç ile başlayan\n" +
                 "malzemelerin yerine geçer; böylece tüm kırılan parçalar aynı kırık beton görünümünü alır. Boşsa dokunulmaz.")]
        public Material FractureInteriorMaterial;
        [Tooltip("Arayüz yazı tipi (isteğe bağlı). Boşsa Unity'nin varsayılan yazı tipi kullanılır.")]
        public Font UiFont;

        [Header("Ses")]
        [Tooltip("Tüm oyun sesleri (Data/SoundBank). Boş klipler sessizce atlanır.")]
        public SoundBankSO Sounds;

        [Header("Ölçekleme")]
        [Tooltip("Açık: yapı prefabları yuvalarına esnetilir (her boyuttaki model çalışır).\n" +
                 "Kapalı: prefab ölçeği korunur, sadece ortalanır; modeller gerçek ölçüde hazırlanmalı.")]
        public bool FitStructureToSlot = true;

        [Header("Eşyalar")]
        public List<ItemDefinitionSO> Items = new List<ItemDefinitionSO>();

        public GameObject ResolveBearingColumn() => BearingColumnPrefab != null ? BearingColumnPrefab : ColumnPrefab;

        /// <summary>
        /// <paramref name="roll"/> (0–1) ile düz duvar ya da bir çeşit seçer. Hiç çeşit yoksa WallPrefab döner
        /// (o da boşsa null → prototip küp).
        /// </summary>
        public GameObject PickWall(double roll)
        {
            float total = WallPrefab != null ? WallPrefabWeight : 0f;
            foreach (var v in WallVariants)
            {
                if (v != null && v.Prefab != null) total += v.Weight;
            }
            if (total <= 0f) return WallPrefab;

            double r = roll * total;
            if (WallPrefab != null)
            {
                r -= WallPrefabWeight;
                if (r < 0) return WallPrefab;
            }
            GameObject last = WallPrefab;
            foreach (var v in WallVariants)
            {
                if (v == null || v.Prefab == null || v.Weight <= 0f) continue;
                last = v.Prefab;
                r -= v.Weight;
                if (r < 0) return v.Prefab;
            }
            return last; // kayan nokta yuvarlaması
        }

        /// <summary>Asset atanmamışsa kullanılan, tamamen prototip küplerinden oluşan içerik.</summary>
        public static GameContentSO CreateDefault()
        {
            var c = CreateInstance<GameContentSO>();
            c.name = "DefaultContent";
            c.Items.AddRange(CreateDefaultItems());
            return c;
        }

        public static ItemDefinitionSO[] CreateDefaultItems()
        {
            return new[]
            {
                ItemDefinitionSO.Create("Buzdolabı", ItemSize.Big, 90f, new Vector3(0.8f, 1.8f, 0.7f), new Color(0.92f, 0.95f, 0.98f)),
                ItemDefinitionSO.Create("Kitaplık", ItemSize.Big, 40f, new Vector3(1.2f, 1.9f, 0.4f), new Color(0.55f, 0.35f, 0.2f), 3),
                ItemDefinitionSO.Create("Koli", ItemSize.Small, 8f, new Vector3(0.5f, 0.5f, 0.5f), new Color(0.85f, 0.65f, 0.35f)),
                ItemDefinitionSO.Create("Lamba", ItemSize.Small, 4f, new Vector3(0.3f, 0.6f, 0.3f), new Color(0.3f, 0.75f, 0.85f)),
                CreateGasTank(),
            };
        }

        /// <summary>Gaz tüpü (GDD): serbest kalırsa ikinci patlama; mutlaka sabitlenmeli. İkinci kattan itibaren çıkar.</summary>
        public static ItemDefinitionSO CreateGasTank()
        {
            var d = ItemDefinitionSO.Create("Gaz tüpü", ItemSize.Small, 15f, new Vector3(0.35f, 0.9f, 0.35f),
                new Color(0.9f, 0.2f, 0.15f));
            d.Volatile = true;
            d.MinFloor = 1;
            d.SpawnWeight = 0.6f;
            return d;
        }

        /// <summary>Verilen boyut ve kat için ağırlıklı rastgele eşya seçer.</summary>
        public ItemDefinitionSO Pick(ItemSize size, int floorNumber, System.Random rng)
        {
            float total = 0f;
            foreach (var d in Items)
            {
                if (IsEligible(d, size, floorNumber)) total += d.SpawnWeight;
            }
            if (total <= 0f) return null;

            double roll = rng.NextDouble() * total;
            ItemDefinitionSO last = null;
            foreach (var d in Items)
            {
                if (!IsEligible(d, size, floorNumber)) continue;
                last = d;
                roll -= d.SpawnWeight;
                if (roll <= 0) return d;
            }
            return last; // kayan nokta yuvarlaması
        }

        private static bool IsEligible(ItemDefinitionSO d, ItemSize size, int floorNumber)
        {
            return d != null && d.Size == size && d.SpawnWeight > 0f && floorNumber >= d.MinFloor;
        }
    }
}
