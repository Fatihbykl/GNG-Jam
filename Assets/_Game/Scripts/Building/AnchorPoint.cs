using UnityEngine;

namespace KatKat
{
    public enum AnchorKind
    {
        Column,
        FloorRing
    }

    /// <summary>
    /// İplerin bağlanabildiği tek yapı noktası (kolon ya da zemin halkası).
    /// Kolon üstündekiler kolonun Rigidbody'sine pinlenir; kolon düşerse ip de onunla gider.
    /// </summary>
    public class AnchorPoint : MonoBehaviour
    {
        public AnchorKind Kind;
        public int Index;

        public Rigidbody AttachedBody { get; private set; }

        /// <summary>Kolon ankrajları için bağlı olduğu kolon.</summary>
        public StructuralPiece Column { get; private set; }

        /// <summary>Tensio pininin hedefi: varsa bağlı Rigidbody, yoksa kendisi (statik).</summary>
        public Transform PinTarget => AttachedBody != null ? AttachedBody.transform : transform;

        private void Awake()
        {
            AttachedBody = GetComponentInParent<Rigidbody>();
            Column = GetComponentInParent<StructuralPiece>();
        }

        private const float PickRadius = 0.26f;

        /// <param name="prefab">Görsel model (isteğe bağlı). Kendi collider'ları silinir; tıklama küresi koddan eklenir.</param>
        /// <param name="rotation">Kolon ankrajlarında +Z kat merkezine bakar.</param>
        public static AnchorPoint Create(AnchorKind kind, Transform parent, Vector3 worldPos, Quaternion rotation, int index,
            GameObject prefab)
        {
            string name = kind == AnchorKind.Column ? "ColumnAnchor" : "FloorRing";
            GameObject go;
            if (prefab != null)
            {
                go = PrefabPlacement.SpawnVisual(prefab, name, parent, worldPos, rotation);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = name;
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.SetParent(parent, true);
                go.transform.SetPositionAndRotation(worldPos, rotation);
                var ps = parent.lossyScale;
                go.transform.localScale = new Vector3(0.22f / ps.x, 0.22f / ps.y, 0.22f / ps.z);
                go.GetComponent<Renderer>().sharedMaterial = GameMaterials.Get(GameMaterials.AnchorColor);
            }

            var col = go.AddComponent<SphereCollider>();
            col.isTrigger = true;
            var s = go.transform.lossyScale;
            col.radius = PickRadius / Mathf.Max(1e-4f, Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));

            var anchor = go.AddComponent<AnchorPoint>();
            anchor.Kind = kind;
            anchor.Index = index;
            return anchor;
        }
    }
}
