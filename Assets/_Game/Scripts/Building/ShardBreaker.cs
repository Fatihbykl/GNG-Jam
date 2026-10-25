using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Sağlam/kırık çiftinden kopan tek parça (ör. kolonun tuğlası). Sert çarpmada ya da şarjın dibinde
    /// <see cref="GameContentSO.BrokenBrickPrefabs"/> içinden bir kırık versiyonla değişir. Kırık versiyonun hücreleri
    /// tuğlanın sınırlarına sığdırılır, hızını devralır ve puanlamada aynı gruba sayılır.
    /// </summary>
    public class ShardBreaker : MonoBehaviour
    {
        private const float CellScatter = 1.2f;
        private const float CellSpin = 6f;

        private static readonly List<MeshFilter> Cells = new List<MeshFilter>();

        private Floor _floor;
        private GameConfig _cfg;
        private bool _broken;

        public static ShardBreaker Add(GameObject go, Floor floor, GameConfig cfg)
        {
            var b = PrefabPlacement.GetOrAdd<ShardBreaker>(go);
            b._floor = floor;
            b._cfg = cfg;
            return b;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.relativeVelocity.magnitude >= _cfg.brickBreakSpeed) Break();
        }

        public void Break()
        {
            if (_broken || _floor == null || _floor.Content == null) return;
            var prefabs = _floor.Content.BrokenBrickPrefabs;
            if (prefabs.Count == 0 || _floor.BrokenShards >= _cfg.maxBrokenBricks) return;
            var prefab = prefabs[Random.Range(0, prefabs.Count)];
            if (prefab == null || !TryGetComponent<MeshFilter>(out var mf) || mf.sharedMesh == null) return;

            var root = Instantiate(prefab);
            root.GetComponentsInChildren(Cells);
            Cells.RemoveAll(c => c.sharedMesh == null);
            if (Cells.Count == 0)
            {
                Destroy(root);
                return;
            }

            _broken = true;
            _floor.BrokenShards++;

            // Hücrelerin ortak ebeveyni, tuğla mesh'inin uzayıdır (FBX kökünün dönüşü/ölçeği burada önemsizleşir).
            var frame = Cells[0].transform.parent != null ? Cells[0].transform.parent : root.transform;
            if (frame != root.transform)
            {
                frame.SetParent(null, true);
                Destroy(root);
            }
            Bounds cells = MeasureCells(frame);
            Bounds brick = mf.sharedMesh.bounds;
            Vector3 scale = Vector3.Scale(transform.lossyScale, PrefabPlacement.SafeDivide(brick.size, cells.size));
            frame.rotation = transform.rotation;
            frame.localScale = scale;
            frame.position = transform.TransformPoint(brick.center) - frame.rotation * Vector3.Scale(scale, cells.center);
            frame.SetParent(_floor.transform, true);
            frame.name = name + "_Broken";

            var body = GetComponent<Rigidbody>();
            Vector3 velocity = body != null ? body.linearVelocity : Vector3.zero;
            Vector3 angular = body != null ? body.angularVelocity : Vector3.zero;
            float mass = body != null ? body.mass : 1f;
            int group = TryGetComponent<Fragment>(out var fragment) ? fragment.Group : GetInstanceID();
            float totalVolume = 0f;
            foreach (var c in Cells) totalVolume += Volume(c);

            foreach (var c in Cells)
            {
                var go = c.gameObject;
                foreach (var col in go.GetComponents<Collider>()) Destroy(col);
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = c.sharedMesh;
                mc.convex = true;

                float share = totalVolume > 1e-6f ? Volume(c) / totalVolume : 1f / Cells.Count;
                var rb = PrefabPlacement.EnsureBody(go, Mathf.Max(_cfg.brickCellMinMass, mass * share), continuous: false);
                rb.isKinematic = false;
                Vector3 away = c.transform.TransformPoint(c.sharedMesh.bounds.center) - transform.position;
                rb.linearVelocity = velocity + away.normalized * CellScatter * Random.value;
                rb.angularVelocity = angular + Random.insideUnitSphere * CellSpin;

                Fragment.MarkGroup(go, group);
                _floor.Bodies.Add(rb);
            }

            if (body != null) _floor.Bodies.Remove(body);
            gameObject.SetActive(false);
            Destroy(gameObject);
        }

        /// <summary>Hücrelerin <paramref name="frame"/> yerel uzayındaki (ölçeksiz) birleşik sınırları.</summary>
        private static Bounds MeasureCells(Transform frame)
        {
            bool any = false;
            var bounds = new Bounds();
            Matrix4x4 toFrame = frame.worldToLocalMatrix;
            foreach (var c in Cells)
            {
                Matrix4x4 m = toFrame * c.transform.localToWorldMatrix;
                Bounds mb = c.sharedMesh.bounds;
                Vector3 center = mb.center, e = mb.extents;
                for (int i = 0; i < 8; i++)
                {
                    var corner = center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                    var p = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else bounds.Encapsulate(p);
                }
            }
            return bounds;
        }

        private static float Volume(MeshFilter mf)
        {
            Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
            return Mathf.Abs(s.x * s.y * s.z);
        }
    }
}
