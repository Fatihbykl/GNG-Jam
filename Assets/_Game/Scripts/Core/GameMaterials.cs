using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Low-poly düz renk paleti. Renk başına tek URP Lit materyali önbelleğe alınır.
    /// </summary>
    public static class GameMaterials
    {
        public static readonly Color Concrete = new Color(0.82f, 0.80f, 0.76f);
        public static readonly Color ColumnColor = new Color(0.62f, 0.64f, 0.70f);
        public static readonly Color BearingColumnColor = new Color(0.27f, 0.33f, 0.48f);
        public static readonly Color CollapseArrowColor = new Color(1f, 0.55f, 0.1f);
        public static readonly Color WallColor = new Color(0.95f, 0.87f, 0.70f);
        public static readonly Color TowerColor = new Color(0.25f, 0.27f, 0.33f);
        public static readonly Color NeighborColor = new Color(0.45f, 0.55f, 0.70f);
        public static readonly Color AnchorColor = new Color(1f, 0.82f, 0.1f);
        public static readonly Color ChargeColor = new Color(0.9f, 0.15f, 0.15f);
        public static readonly Color TapeYellow = new Color(1f, 0.85f, 0f);
        public static readonly Color TapeBlack = new Color(0.08f, 0.08f, 0.08f);

        private static readonly Dictionary<Color, Material> Cache = new Dictionary<Color, Material>();
        private static Shader _lit;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Cache.Clear();
            _lit = null;
        }

        public static Material Get(Color color)
        {
            if (Cache.TryGetValue(color, out var mat) && mat != null) return mat;

            if (_lit == null) _lit = Shader.Find("Universal Render Pipeline/Lit");
            if (_lit == null) _lit = Shader.Find("Standard");

            mat = new Material(_lit) { name = "KatKat_" + ColorUtility.ToHtmlStringRGB(color) };
            mat.SetColor("_BaseColor", color);
            mat.color = color;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);
            Cache[color] = mat;
            return mat;
        }

        public static GameObject CreateBox(string name, Transform parent, Vector3 position, Vector3 size, Color color,
            bool withCollider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = Get(color);
            if (!withCollider) Object.Destroy(go.GetComponent<Collider>());
            return go;
        }

        public static Rigidbody AddBody(GameObject go, float mass, bool continuous = false)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = continuous
                ? CollisionDetectionMode.ContinuousDynamic
                : CollisionDetectionMode.Discrete;
            rb.isKinematic = true;
            return rb;
        }
    }
}
