using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Bir eşya türü. Prefab, modelin kendisidir; Rigidbody, collider ve Item bileşeni yoksa kod ekler.
    /// Prefab içinde kendi Rigidbody'si olan alt objeler "gevşek parça" sayılır (ör. kitaplıktaki kitaplar):
    /// eşyaya kırılabilir bir eklemle bağlanırlar ve devrilince dağılırlar.
    /// </summary>
    [CreateAssetMenu(menuName = "Kat Kat Yıkım/Item Definition", fileName = "Item")]
    public class ItemDefinitionSO : ScriptableObject
    {
        public string DisplayName = "Eşya";
        [Tooltip("Modelin prefabı. Boşsa aşağıdaki yedek kutu kullanılır.")]
        public GameObject Prefab;
        public ItemSize Size = ItemSize.Small;
        public float Mass = 10f;
        public bool CanBeTied = true;

        [Header("Yerleşim")]
        [Tooltip("Zeminde kapladığı yarıçap (m). 0 = model sınırlarından otomatik.")]
        public float FootprintRadius;
        [Min(0f)] public float SpawnWeight = 1f;
        [Tooltip("Bu kattan önce çıkmaz (0 = ilk kattan itibaren).")]
        public int MinFloor;

        [Header("Gevşek parçalar")]
        public float LoosePartBreakForce = 120f;
        public float LoosePartBreakTorque = 60f;
        [Tooltip("Sadece yedek kutunun parçaları için. Prefab parçalarında kendi Rigidbody kütlesi kullanılır.")]
        public float LoosePartMass = 1.5f;

        [Header("Prefab yoksa")]
        public Vector3 FallbackSize = new Vector3(0.5f, 0.5f, 0.5f);
        public Color FallbackColor = Color.white;
        [Tooltip("Yedek kutunun üstüne konacak gevşek parça sayısı.")]
        public int FallbackLooseParts;

        private float _cachedRadius = -1f;

        public float ResolveFootprintRadius()
        {
            if (FootprintRadius > 0f) return FootprintRadius;
            if (_cachedRadius > 0f) return _cachedRadius;

            Vector3 size = FallbackSize;
            if (Prefab != null) size = PrefabPlacement.MeasureLocal(Prefab.transform).size;
            _cachedRadius = Mathf.Max(0.2f, 0.5f * Mathf.Sqrt(size.x * size.x + size.z * size.z) + 0.05f);
            return _cachedRadius;
        }

        private void OnValidate() => _cachedRadius = -1f;

        public static ItemDefinitionSO Create(string name, ItemSize size, float mass, Vector3 fallbackSize, Color color,
            int looseParts = 0)
        {
            var d = CreateInstance<ItemDefinitionSO>();
            d.name = name;
            d.DisplayName = name;
            d.Size = size;
            d.Mass = mass;
            d.FallbackSize = fallbackSize;
            d.FallbackColor = color;
            d.FallbackLooseParts = looseParts;
            return d;
        }
    }
}
