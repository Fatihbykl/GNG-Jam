using UnityEngine;

namespace KatKat
{
    [CreateAssetMenu(menuName = "Kat Kat Yıkım/Rope Type", fileName = "RopeType")]
    public class RopeTypeSO : ScriptableObject
    {
        public string DisplayName = "İp";
        [Tooltip("Bütçeden metre başına düşülen miktar.")]
        public float CostPerMeter = 1f;
        [Tooltip("Pin uçlarında ölçülen kuvvet (N) bu değeri aşınca ip kopar.")]
        public float BreakForce = 1500f;
        [Tooltip("Yedek kural: dinlenme uzunluğuna göre bu oranın üstünde uzarsa kopar.")]
        public float MaxStretchRatio = 1.6f;
        [Tooltip("Tensio Compliance. 0 = sert, büyüdükçe esner (elastik ip).")]
        public float Compliance;
        public float ParticleMass = 0.05f;
        public float Radius = 0.03f;
        public Color Color = Color.white;
        public Material Material;

        [Header("File")]
        [Tooltip("0'dan büyükse bu bir filedir: bağlanan eşyanın bu yarıçapındaki (m) bağsız küçük eşyalar ona paketlenir.")]
        public float NetRadius;
        [Tooltip("Filenin en fazla kaç küçük eşyayı toplayacağı.")]
        public int NetMaxItems = 3;
        [Tooltip("Pakete katılan her eşya için bütçeden düşülen ek miktar.")]
        public float NetCostPerItem = 1f;

        public bool IsNet => NetRadius > 0f;

        public Material ResolveMaterial() => Material != null ? Material : GameMaterials.Get(Color);

        public static RopeTypeSO Create(string name, float cost, float breakForce, float stretch, float compliance,
            float particleMass, float radius, Color color)
        {
            var t = CreateInstance<RopeTypeSO>();
            t.name = name;
            t.DisplayName = name;
            t.CostPerMeter = cost;
            t.BreakForce = breakForce;
            t.MaxStretchRatio = stretch;
            t.Compliance = compliance;
            t.ParticleMass = particleMass;
            t.Radius = radius;
            t.Color = color;
            return t;
        }

        /// <summary>Oyundaki tek ip; GameManager'a asset atanmamışsa kullanılır. İnce ip ile çelik halat arası dayanım.</summary>
        public static RopeTypeSO CreateDefault()
        {
            return Create("İp", 1.2f, 3500f, 1.5f, 0f, 0.05f, 0.03f, new Color(0.93f, 0.85f, 0.65f));
        }

        /// <summary>GDD'deki ip türleri (artık oyunda yalnızca tek ip var; asset üretimi için duruyor).</summary>
        public static RopeTypeSO[] CreateDefaults()
        {
            return new[]
            {
                Create("İnce ip", 1f, 1400f, 1.5f, 0f, 0.04f, 0.025f, new Color(0.93f, 0.85f, 0.65f)),
                Create("Çelik halat", 2.5f, 9000f, 1.3f, 0f, 0.3f, 0.045f, new Color(0.55f, 0.58f, 0.62f)),
                Create("Elastik ip", 1.5f, 3500f, 2.2f, 0.5f, 0.06f, 0.035f, new Color(0.35f, 0.85f, 0.45f)),
                CreateNet(),
            };
        }

        /// <summary>File: orta dayanım; birçok küçük eşyayı tek seferde sarar (GDD).</summary>
        public static RopeTypeSO CreateNet()
        {
            var t = Create("File", 1.5f, 3000f, 1.6f, 0.2f, 0.05f, 0.03f, new Color(0.95f, 0.55f, 0.85f));
            t.NetRadius = 1.6f;
            t.NetMaxItems = 3;
            t.NetCostPerItem = 1f;
            return t;
        }
    }
}
