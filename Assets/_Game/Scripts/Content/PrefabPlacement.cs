using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Model prefablarını oyuna sokar: sınırlarını ölçer, yuvasına ortalar (isteğe bağlı sığdırır),
    /// collider yoksa kutu collider ekler, dinamik cisimler için collider'ları uygun hale getirir.
    /// Modelin pivotu ya da ölçeği ne olursa olsun çalışacak şekilde tasarlandı.
    /// </summary>
    public static class PrefabPlacement
    {
        private static readonly List<MeshFilter> MeshFilters = new List<MeshFilter>();
        private static readonly List<SkinnedMeshRenderer> Skinned = new List<SkinnedMeshRenderer>();

        /// <summary>
        /// Kökün pivotuna göre, kökün kendi ölçeği uygulanmış ve dönüşsüz uzaydaki görsel sınırlar.
        /// Hem sahnedeki örneklerde hem prefab asset'lerinde çalışır (Renderer.bounds'a güvenmez).
        /// </summary>
        public static Bounds MeasureLocal(Transform root, ICollection<Transform> exclude = null)
        {
            Matrix4x4 toRoot = Matrix4x4.Scale(root.localScale) * root.worldToLocalMatrix;
            bool any = false;
            var bounds = new Bounds();

            void Add(Mesh mesh, Transform t)
            {
                if (mesh == null || IsUnder(t, root, exclude)) return;
                Matrix4x4 m = toRoot * t.localToWorldMatrix;
                Bounds mb = mesh.bounds;
                Vector3 c = mb.center, e = mb.extents;
                for (int i = 0; i < 8; i++)
                {
                    var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                    var p = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else bounds.Encapsulate(p);
                }
            }

            root.GetComponentsInChildren(true, MeshFilters);
            foreach (var mf in MeshFilters) Add(mf.sharedMesh, mf.transform);
            root.GetComponentsInChildren(true, Skinned);
            foreach (var smr in Skinned) Add(smr.sharedMesh, smr.transform);

            return any ? bounds : new Bounds(Vector3.zero, Vector3.one * 0.5f);
        }

        /// <summary>
        /// Prefabı örnekler; görsel merkezini <paramref name="center"/> noktasına getirir.
        /// <paramref name="fit"/> açıksa yerel eksenlerde <paramref name="slotSize"/> boyutuna esnetir.
        /// </summary>
        public static GameObject Place(GameObject prefab, string name, Transform parent, Vector3 center, Quaternion rotation,
            Vector3 slotSize, bool fit)
        {
            var go = Object.Instantiate(prefab, parent);
            go.name = name;
            var t = go.transform;
            Fracturable.HideFracturedSet(go);
            Bounds b = MeasureLocal(t);
            EnsureCollider(go, b, null);

            Vector3 scale = t.localScale;
            Vector3 k = fit ? SafeDivide(slotSize, b.size) : Vector3.one;
            t.localScale = Vector3.Scale(scale, k);
            Vector3 offset = Vector3.Scale(b.center, k);
            t.SetPositionAndRotation(center - rotation * offset, rotation);
            return go;
        }

        /// <summary>Prefabı tabanının ortası <paramref name="floorPoint"/> üstüne oturacak şekilde yerleştirir.</summary>
        public static GameObject PlaceOnFloor(GameObject prefab, string name, Transform parent, Vector3 floorPoint,
            Quaternion rotation, out Bounds localBounds, ICollection<Transform> exclude = null)
        {
            var go = Object.Instantiate(prefab, parent);
            go.name = name;
            var t = go.transform;
            localBounds = MeasureLocal(t, exclude);
            var pivotOffset = new Vector3(localBounds.center.x, localBounds.min.y, localBounds.center.z);
            t.SetPositionAndRotation(floorPoint + Vector3.up * 0.01f - rotation * pivotOffset, rotation);
            return go;
        }

        /// <summary>
        /// Sadece görsel amaçlı bir prefab (ankraj, şarj işareti). Dünya ölçeğini korur, collider'ları kaldırır.
        /// </summary>
        public static GameObject SpawnVisual(GameObject prefab, string name, Transform parent, Vector3 position,
            Quaternion rotation)
        {
            var go = Object.Instantiate(prefab, position, rotation);
            go.name = name;
            go.transform.SetParent(parent, true);
            foreach (var col in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(rb);
            return go;
        }

        /// <summary>
        /// Hiyerarşide (hariç tutulanlar dışında) collider yoksa ölçülen sınırlardan kutu collider ekler.
        /// Dinamik cisimlerde çalışmayan convex olmayan MeshCollider'ları convex yapar.
        /// </summary>
        public static void EnsureCollider(GameObject go, Bounds localBounds, ICollection<Transform> exclude)
        {
            bool hasCollider = false;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                if (IsUnder(col.transform, go.transform, exclude)) continue;
                if (col is MeshCollider mc && !mc.convex) mc.convex = true;
                hasCollider = true;
            }
            if (hasCollider) return;

            var box = go.AddComponent<BoxCollider>();
            Vector3 s = go.transform.localScale;
            box.center = SafeDivide(localBounds.center, s);
            box.size = SafeDivide(localBounds.size, s);
        }

        public static Rigidbody EnsureBody(GameObject go, float mass, bool continuous)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = continuous
                ? CollisionDetectionMode.ContinuousDynamic
                : CollisionDetectionMode.Discrete;
            rb.isKinematic = true;
            return rb;
        }

        public static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        private static bool IsUnder(Transform t, Transform root, ICollection<Transform> exclude)
        {
            if (exclude == null || exclude.Count == 0) return false;
            for (var p = t; p != null && p != root; p = p.parent)
            {
                if (exclude.Contains(p)) return true;
            }
            return false;
        }

        public static Vector3 SafeDivide(Vector3 a, Vector3 b)
        {
            return new Vector3(
                Mathf.Abs(b.x) > 1e-4f ? a.x / b.x : 1f,
                Mathf.Abs(b.y) > 1e-4f ? a.y / b.y : 1f,
                Mathf.Abs(b.z) > 1e-4f ? a.z / b.z : 1f);
        }
    }
}
