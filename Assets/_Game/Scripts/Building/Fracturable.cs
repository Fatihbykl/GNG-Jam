using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Blender Cell Fracture ile önceden parçalanmış duvar/tavan modeli. Prefab, kökün altında her biri ayrı
    /// mesh olan parçalardan oluşur. Sağlamken tek Rigidbody ve kök collider'ı ile tek parça gibi davranır;
    /// sert bir çarpmada ya da yakındaki şarj patlayınca her parça kendi Rigidbody'sine kavuşur.
    /// Parçalar kökün altında kalır (esnetilmiş ölçek bozulmasın diye); kök kinematik olup yerinde bekler.
    /// </summary>
    [RequireComponent(typeof(StructuralPiece))]
    public class Fracturable : MonoBehaviour
    {
        private const float AngularKick = 3f;

        public bool IsShattered { get; private set; }

        private StructuralPiece _piece;
        private Floor _floor;
        private GameConfig _cfg;
        private readonly List<MeshFilter> _shards = new List<MeshFilter>();

        /// <summary>Modelde en az iki parça mesh varsa parçanın kırılabilir olmasını sağlar.</summary>
        public static Fracturable TryAdd(StructuralPiece piece, Floor floor, GameConfig cfg)
        {
            var shards = new List<MeshFilter>();
            foreach (var mf in piece.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.transform != piece.transform && mf.sharedMesh != null) shards.Add(mf);
            }
            if (shards.Count < 2) return null;

            var f = PrefabPlacement.GetOrAdd<Fracturable>(piece.gameObject);
            f._piece = piece;
            f._floor = floor;
            f._cfg = cfg;
            f._shards.Clear();
            f._shards.AddRange(shards);
            return f;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (IsShattered || _piece == null || _piece.Body.isKinematic) return;
            if (collision.relativeVelocity.magnitude >= _cfg.shatterImpactSpeed) Shatter(null, 0f);
        }

        /// <param name="blastOrigin">Patlama noktası; verilirse parçalar buradan dışarı savrulur.</param>
        /// <param name="kick">Patlama merkezindeki hız değişimi (m/s), uzaklıkla azalır.</param>
        public void Shatter(Vector3? blastOrigin, float kick)
        {
            if (IsShattered || _piece == null) return;
            IsShattered = true;

            _piece.DetachAll();
            var body = _piece.Body;
            Vector3 velocity = body.isKinematic ? Vector3.zero : body.linearVelocity;
            Vector3 angular = body.isKinematic ? Vector3.zero : body.angularVelocity;
            Vector3 com = body.worldCenterOfMass;

            foreach (var col in GetComponents<Collider>()) col.enabled = false;
            body.isKinematic = true;
            body.detectCollisions = false;
            _floor.Bodies.Remove(body);

            float totalVolume = 0f;
            foreach (var mf in _shards) totalVolume += Volume(mf);

            foreach (var mf in _shards)
            {
                if (mf == null) continue;
                var go = mf.gameObject;
                if (go.GetComponent<Collider>() == null)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }
                foreach (var col in go.GetComponents<Collider>())
                {
                    if (col is MeshCollider mc && !mc.convex) mc.convex = true;
                    col.enabled = true;
                }

                float share = totalVolume > 1e-6f ? Volume(mf) / totalVolume : 1f / _shards.Count;
                var rb = PrefabPlacement.EnsureBody(go, Mathf.Max(_cfg.shardMinMass, body.mass * share), continuous: false);
                rb.isKinematic = false;

                Vector3 center = mf.transform.TransformPoint(mf.sharedMesh.bounds.center);
                Vector3 v = velocity + Vector3.Cross(angular, center - com);
                if (blastOrigin.HasValue)
                {
                    Vector3 away = center - blastOrigin.Value;
                    float falloff = 1f - Mathf.Clamp01(away.magnitude / _cfg.explosionRadius);
                    v += (away.normalized + Vector3.up * _cfg.explosionUpward) * (kick * falloff);
                }
                rb.linearVelocity = v;
                rb.angularVelocity = angular + Random.insideUnitSphere * AngularKick;

                go.AddComponent<Fragment>().Source = _piece;
                _floor.Bodies.Add(rb);
            }
        }

        private static float Volume(MeshFilter mf)
        {
            if (mf == null || mf.sharedMesh == null) return 0f;
            Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
            return Mathf.Abs(s.x * s.y * s.z);
        }
    }
}
