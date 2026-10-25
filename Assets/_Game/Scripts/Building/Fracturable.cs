using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Blender Cell Fracture ile önceden parçalanmış duvar/tavan/kolon modeli. Prefab, kökün altında her biri ayrı
    /// mesh olan parçalardan oluşur. Sağlamken tek Rigidbody ve kök collider'ı ile tek parça gibi davranır;
    /// sert bir çarpmada ya da yakındaki şarj patlayınca her parça kendi Rigidbody'sine kavuşur.
    /// Parçalar kökün altında kalır (esnetilmiş ölçek bozulmasın diye); kök kinematik olup yerinde bekler.
    /// </summary>
    [RequireComponent(typeof(StructuralPiece))]
    public class Fracturable : MonoBehaviour
    {
        private const float AngularKick = 3f;

        /// <summary>
        /// Açıksa sert çarpmada ve yakındaki şarj patlamasında kırılır. Kolonlarda kapalı: onları yalnızca
        /// kendi şarjı kırar, yoksa ankrajı ve ipi havada kalırdı.
        /// </summary>
        public bool ShatterOnImpact { get; private set; }
        public bool IsShattered { get; private set; }

        private StructuralPiece _piece;
        private Floor _floor;
        private GameConfig _cfg;
        private readonly List<MeshFilter> _shards = new List<MeshFilter>();
        /// <summary>Sağlam/kırık çifti olan modelde sağlam mesh'in renderer'ları; kırılınca gizlenir.</summary>
        private readonly List<Renderer> _intact = new List<Renderer>();
        private float _totalVolume = -1f;

        /// <summary>
        /// Modelde adı "Fractured" ile biten bir obje varsa (ör. Wall_Fractured) model sağlam/kırık çiftidir:
        /// parçalar o objenin altındadır, geri kalan mesh'ler (ör. Wall_Intact) sağlam görünümdür.
        /// </summary>
        public static Transform FindFracturedSet(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != root && t.name.EndsWith("fractured", System.StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        /// <summary>Sağlam/kırık çiftinde kırık parçaları gizler (kırılabilir olmayan taşıyıcı kolonlar da dahil).</summary>
        public static void HideFracturedSet(GameObject go)
        {
            var set = FindFracturedSet(go.transform);
            if (set == null) return;
            foreach (var r in set.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        }

        /// <summary>Modelde en az iki parça mesh varsa parçanın kırılabilir olmasını sağlar.</summary>
        /// <remarks>Ankraj ve şarj işareti gibi görsel alt objeler eklenmeden önce çağrılmalı; yoksa parça sanılırlar.</remarks>
        public static Fracturable TryAdd(StructuralPiece piece, Floor floor, GameConfig cfg, bool shatterOnImpact = true)
        {
            var set = FindFracturedSet(piece.transform);
            var shards = new List<MeshFilter>();
            foreach (var mf in (set != null ? set : piece.transform).GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.transform != piece.transform && mf.sharedMesh != null) shards.Add(mf);
            }
            if (shards.Count < 2) return null;

            var f = PrefabPlacement.GetOrAdd<Fracturable>(piece.gameObject);
            f._piece = piece;
            f._floor = floor;
            f._cfg = cfg;
            f.ShatterOnImpact = shatterOnImpact;
            f._shards.Clear();
            f._shards.AddRange(shards);
            f._intact.Clear();
            if (set != null)
            {
                foreach (var r in piece.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.transform.IsChildOf(set)) f._intact.Add(r);
                }
            }
            if (floor.Content != null) ApplyInteriorMaterial(shards, floor.Content.FractureInteriorMaterial);
            return f;
        }

        private static readonly string[] InteriorNames = { "interior", "inner", "ic", "iç", "inside" };

        /// <summary>Kırık yüz malzemelerini (adı Interior/Inner/Ic/İç ile başlayan) ortak malzemeyle değiştirir.</summary>
        private static void ApplyInteriorMaterial(List<MeshFilter> shards, Material interior)
        {
            if (interior == null) return;
            foreach (var mf in shards)
            {
                if (!mf.TryGetComponent<Renderer>(out var r)) continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || !IsInterior(mats[i].name)) continue;
                    mats[i] = interior;
                    changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        private static bool IsInterior(string name)
        {
            string n = name.Replace(" (Instance)", "").Trim().ToLowerInvariant();
            foreach (var key in InteriorNames)
            {
                if (!n.StartsWith(key)) continue;
                // "ic" gibi kısa anahtarlar "icon" ile karışmasın: anahtardan sonra harf gelmemeli
                if (n.Length == key.Length || !char.IsLetter(n[key.Length])) return true;
            }
            return false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!ShatterOnImpact || IsShattered || _piece == null || _piece.Body.isKinematic) return;
            if (collision.relativeVelocity.magnitude >= _cfg.shatterImpactSpeed) Shatter(null, 0f);
        }

        /// <param name="blastOrigin">Patlama noktası; verilirse parçalar buradan dışarı savrulur.</param>
        /// <param name="kick">Patlama merkezindeki hız değişimi (m/s), uzaklıkla azalır.</param>
        /// <param name="topple">
        /// Devrilme hızı: parçalar bu yöne, yükseklikleriyle artan hızla savrulur (tepe en hızlı). Duvarın patlama
        /// sırasına göre içe/dışa düşmesi için.
        /// </param>
        public void Shatter(Vector3? blastOrigin, float kick, Vector3 topple = default)
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

            float baseY = _piece.WorldCenter.y - _piece.SlotSize.y * 0.5f;
            float height = Mathf.Max(0.1f, _piece.SlotSize.y);
            float totalVolume = 0f;
            foreach (var mf in _shards) totalVolume += Volume(mf);
            RevealShards();
            SpawnDust();
            bool breakable = BreakableShards;

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
                if (topple != Vector3.zero)
                {
                    float k = Mathf.Clamp01((center.y - baseY) / height);
                    v += topple * Mathf.Lerp(0.3f, 1f, k);
                }
                rb.linearVelocity = v;
                rb.angularVelocity = angular + Random.insideUnitSphere * AngularKick;

                Fragment.Mark(go, _piece);
                _floor.Bodies.Add(rb);

                if (!breakable) continue;
                var breaker = ShardBreaker.Add(go, _floor, _cfg);
                if (blastOrigin.HasValue && Vector3.Distance(center, blastOrigin.Value) <= _cfg.brickBreakBlastRadius)
                    breaker.Break();
            }
        }

        /// <summary>Sağlam/kırık çiftinin parçaları (ör. kolonun tuğlaları) kırık versiyonlarına bölünebilir.</summary>
        private bool BreakableShards => _intact.Count > 0 && _floor.Content != null && _floor.Content.BrokenBrickPrefabs.Count > 0;

        /// <summary>Sağlam mesh'i gizleyip parçaları gösterir; ikisi aynı geometri olduğu için geçiş görünmez.</summary>
        private void RevealShards()
        {
            foreach (var r in _intact)
            {
                if (r != null) r.enabled = false;
            }
            foreach (var mf in _shards)
            {
                if (mf != null && mf.TryGetComponent<Renderer>(out var r)) r.enabled = true;
            }
        }

        /// <summary>
        /// Kısmi dökülme (taşıyıcı kolon): <paramref name="cutY"/> üstündeki parçalar kopar, altındakiler parçada kalır.
        /// <paramref name="ragged"/> kadar altındaki bantta parçalar yarı yarıya kopar, böylece tepe düz kesilmez.
        /// Kopan parçalar katın altına taşınır (kök hareket etmeye devam ediyor) ve puanlamaya katılmaz: oyuncunun
        /// elinde olmayan süs döküntüsüdür. Kök collider'ı kalan yüksekliğe indirilir ki kopanlar içinde sıkışmasın.
        /// </summary>
        /// <returns>Kopan parça sayısı.</returns>
        public int ShedAbove(float cutY, float ragged, Vector3 kickOrigin, float kick)
        {
            if (IsShattered || _piece == null || _intact.Count == 0) return 0;

            var body = _piece.Body;
            if (_totalVolume < 0f)
            {
                _totalVolume = 0f;
                foreach (var mf in _shards) _totalVolume += Volume(mf);
            }

            var shed = new List<MeshFilter>();
            foreach (var mf in _shards)
            {
                if (mf == null) continue;
                float y = mf.transform.TransformPoint(mf.sharedMesh.bounds.center).y;
                if (y >= cutY || (y >= cutY - ragged && Random.value < 0.5f)) shed.Add(mf);
            }
            if (shed.Count == 0) return 0;

            RevealShards();
            bool breakable = BreakableShards;
            Vector3 velocity = body.isKinematic ? Vector3.zero : body.linearVelocity;
            Vector3 axis = _piece.WorldCenter;
            var bounds = new Bounds();

            for (int i = 0; i < shed.Count; i++)
            {
                var mf = shed[i];
                _shards.Remove(mf);
                var go = mf.gameObject;
                Vector3 center = mf.transform.TransformPoint(mf.sharedMesh.bounds.center);
                if (i == 0) bounds = new Bounds(center, Vector3.zero);
                else bounds.Encapsulate(center);
                go.transform.SetParent(_floor.transform, true);

                if (go.GetComponent<Collider>() == null) go.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                foreach (var col in go.GetComponents<Collider>())
                {
                    if (col is MeshCollider mc && !mc.convex) mc.convex = true;
                    col.enabled = true;
                }

                float share = _totalVolume > 1e-6f ? Volume(mf) / _totalVolume : 0.03f;
                var rb = PrefabPlacement.EnsureBody(go, Mathf.Max(_cfg.shardMinMass, body.mass * share), continuous: false);
                rb.isKinematic = false;
                // Kolonun ekseninden dışa ve patlamadan uzağa, hafif yukarı: tuğlalar kenardan dökülür
                Vector3 outward = center - axis;
                outward.y = 0f;
                Vector3 away = center - kickOrigin;
                away.y = 0f;
                Vector3 dir = (outward.normalized + away.normalized).normalized + Vector3.up * 0.3f;
                rb.linearVelocity = velocity + dir * (kick * Random.Range(0.5f, 1f));
                rb.angularVelocity = Random.insideUnitSphere * AngularKick;
                Fragment.Mark(go, _piece);
                if (breakable) ShardBreaker.Add(go, _floor, _cfg);
            }

            ShrinkCollider();
            bounds.Expand(0.3f);
            Vfx.Shatter(bounds);
            AudioDirector.Play(Sound.ConcreteBreak, bounds.center);
            return shed.Count;
        }

        /// <summary>Kök kutu collider'ının tepesini kalan parçaların tepesine indirir.</summary>
        private void ShrinkCollider()
        {
            if (!TryGetComponent<BoxCollider>(out var box)) return;
            float top = float.NegativeInfinity;
            Matrix4x4 toLocal = transform.worldToLocalMatrix;
            foreach (var mf in _shards)
            {
                if (mf == null) continue;
                Matrix4x4 m = toLocal * mf.transform.localToWorldMatrix;
                Bounds b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    top = Mathf.Max(top, m.MultiplyPoint3x4(corner).y);
                }
            }
            float bottom = box.center.y - box.size.y * 0.5f;
            if (float.IsNegativeInfinity(top) || top <= bottom) return;
            top = Mathf.Min(top, box.center.y + box.size.y * 0.5f);
            box.center = new Vector3(box.center.x, (bottom + top) * 0.5f, box.center.z);
            box.size = new Vector3(box.size.x, top - bottom, box.size.z);
        }

        private void SpawnDust()
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (var mf in _shards)
            {
                if (mf == null || !mf.TryGetComponent<Renderer>(out var r)) continue;
                if (!any) bounds = r.bounds;
                else bounds.Encapsulate(r.bounds);
                any = true;
            }
            if (!any) return;
            Vfx.Shatter(bounds);
            AudioDirector.Play(Sound.ConcreteBreak, bounds.center);
        }

        private static float Volume(MeshFilter mf)
        {
            if (mf == null || mf.sharedMesh == null) return 0f;
            Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
            return Mathf.Abs(s.x * s.y * s.z);
        }
    }
}
