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

        public static AnchorPoint Create(AnchorKind kind, Transform parent, Vector3 worldPos, int index)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = kind == AnchorKind.Column ? "ColumnAnchor" : "FloorRing";
            go.transform.SetParent(parent, true);
            go.transform.position = worldPos;
            go.transform.localScale = new Vector3(
                0.22f / parent.lossyScale.x, 0.22f / parent.lossyScale.y, 0.22f / parent.lossyScale.z);
            go.GetComponent<Renderer>().sharedMaterial = GameMaterials.Get(GameMaterials.AnchorColor);

            var col = go.GetComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = 1.2f;

            var anchor = go.AddComponent<AnchorPoint>();
            anchor.Kind = kind;
            anchor.Index = index;
            return anchor;
        }
    }
}
