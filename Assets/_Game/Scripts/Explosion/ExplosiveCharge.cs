using System.Collections.Generic;
using Tensio;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Sabit yerleştirilmiş kesici şarj grubu. Bir kolon sırasındaki şarjlı kolonları kapsar; oyuncu sadece
    /// sırasını ve gecikmesini değiştirir. Şarj kolonu keser, cisimlere yalnızca küçük bir itme verir:
    /// çöküşü yerçekimi ve patlama sırası belirler.
    /// </summary>
    public class ExplosiveCharge : MonoBehaviour
    {
        public const float MaxDelay = 2f;
        private const float RubbleMassShare = 0.5f;

        public string Label;
        public Floor Floor;
        [Range(0f, MaxDelay)] public float Delay = 0.4f;
        public readonly List<StructuralPiece> Columns = new List<StructuralPiece>();
        public bool HasDetonated { get; private set; }

        private readonly List<GameObject> _markers = new List<GameObject>();

        public Vector3 LabelPosition
        {
            get
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                foreach (var c in Columns)
                {
                    if (c == null) continue;
                    sum += c.WorldCenter;
                    n++;
                }
                return n > 0 ? sum / n + Vector3.up * 1.2f : transform.position;
            }
        }

        /// <param name="markerPrefab">Kolona takılan patlayıcı modeli (isteğe bağlı).</param>
        public void AddColumn(StructuralPiece column, GameObject markerPrefab)
        {
            Columns.Add(column);
            Vector3 pos = column.WorldCenter + Vector3.down * (column.SlotSize.y * 0.25f);
            if (markerPrefab != null)
            {
                // Kolonun dört yüzüne birer set (arkası yüze, önü dışarı): hangi açıdan bakılsa görünür
                for (int k = 0; k < 4; k++)
                {
                    Quaternion face = column.transform.rotation * Quaternion.Euler(0f, 90f * k, 0f);
                    float half = (k % 2 == 0 ? column.SlotSize.z : column.SlotSize.x) * 0.5f;
                    _markers.Add(PrefabPlacement.SpawnVisual(markerPrefab, "Charge", column.transform,
                        pos + face * Vector3.forward * half, face));
                }
            }
            else
            {
                var size = new Vector3(column.SlotSize.x * 1.25f, 0.35f, column.SlotSize.z * 1.25f);
                var marker = GameMaterials.CreateBox("Charge", null, pos, size, GameMaterials.ChargeColor, false);
                marker.transform.SetParent(column.transform, true);
                _markers.Add(marker);
            }
        }

        public void Detonate(GameConfig config, GameObject vfxPrefab)
        {
            if (HasDetonated) return;
            HasDetonated = true;

            foreach (var m in _markers)
            {
                if (m != null) Destroy(m);
            }
            _markers.Clear();

            var hit = new HashSet<Rigidbody>();

            foreach (var column in Columns)
            {
                if (column == null) continue;
                Vector3 pos = column.WorldCenter;
                // Kolonun taşıdığı tavan döşemeleri de patlamada kırılsın: tepeye yakınlık da sayılır
                Vector3 top = pos + Vector3.up * (column.SlotSize.y * 0.5f);
                SpawnVfx(vfxPrefab, pos);
                Cut(column, pos, config);

                hit.Clear();
                foreach (var col in Physics.OverlapSphere(pos, config.explosionRadius, ~0, QueryTriggerInteraction.Ignore))
                {
                    var rb = col.attachedRigidbody;
                    if (rb != null && !rb.isKinematic && rb.TryGetComponent<Fracturable>(out var fracturable) &&
                        fracturable.ShatterOnImpact && !fracturable.IsShattered &&
                        Mathf.Min(Vector3.Distance(col.ClosestPoint(pos), pos), Vector3.Distance(col.ClosestPoint(top), top)) <= config.shatterBlastRadius)
                    {
                        fracturable.Shatter(pos, config.explosionKick);
                        continue;
                    }
                    if (rb == null || rb.isKinematic || !hit.Add(rb)) continue;
                    // Eşyaları yalnızca kurala bağlı çöküş itkisi kaydırır (CollapseShove), tahmin şaşmasın.
                    // Devrilen duvarların yönü de patlama sırasından gelir; rastgele savurma onu bozmasın.
                    if (rb.TryGetComponent<Item>(out _)) continue;
                    var piece = rb.GetComponentInParent<StructuralPiece>();
                    if (piece != null && piece.FallDirected) continue;
                    rb.AddExplosionForce(config.explosionKick, pos, config.explosionRadius, config.explosionUpward,
                        ForceMode.VelocityChange);
                }

                foreach (var rope in RopeController.AllRopes)
                {
                    rope.AddExplosionForce(config.ropeBlastPower, pos, config.explosionRadius);
                }
            }

            ShakeBearingColumns();
        }

        /// <summary>Taşıyıcı kolonlar, sıranın en yakın kolonuna olan uzaklığa göre tepelerinden tuğla döker.</summary>
        private void ShakeBearingColumns()
        {
            if (Floor == null) return;
            foreach (var bearing in Floor.Columns)
            {
                if (bearing == null || !bearing.IsBearing || !bearing.TryGetComponent<BearingCrumble>(out var crumble)) continue;
                Vector3 nearest = default;
                float best = float.PositiveInfinity;
                foreach (var column in Columns)
                {
                    if (column == null) continue;
                    float d = (column.WorldCenter - bearing.WorldCenter).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    nearest = column.WorldCenter;
                }
                if (!float.IsPositiveInfinity(best)) crumble.OnBlast(nearest);
            }
        }

        /// <summary>
        /// Kolonu keser: tüm eklemler gider. Cell Fracture modeliyse kolon parçalarına ayrılır;
        /// değilse kolon yok olur ve yerine iki moloz parçası kalır. Her iki durumda üstündeki ankraj da yok olur.
        /// </summary>
        private void Cut(StructuralPiece column, Vector3 blastOrigin, GameConfig config)
        {
            if (column.TryGetComponent<Fracturable>(out var fracturable))
            {
                foreach (var anchor in column.GetComponentsInChildren<AnchorPoint>(true)) Destroy(anchor.gameObject);
                fracturable.Shatter(blastOrigin, config.explosionKick);
                return;
            }

            column.DetachAll();

            var t = column.transform;
            Vector3 size = column.SlotSize;
            Vector3 center = column.WorldCenter;
            float mass = column.Body.mass * RubbleMassShare;
            Vector3 chunk = new Vector3(size.x * 0.9f, size.y * 0.45f, size.z * 0.9f);
            var rubblePrefab = Floor != null && Floor.Content != null ? Floor.Content.RubblePrefab : null;

            for (int i = 0; i < 2; i++)
            {
                Vector3 p = center + t.up * (i == 0 ? -size.y * 0.25f : size.y * 0.25f);
                GameObject go;
                if (rubblePrefab != null)
                {
                    go = PrefabPlacement.Place(rubblePrefab, "Rubble", t.parent, p, t.rotation, chunk, fit: true);
                }
                else
                {
                    go = GameMaterials.CreateBox("Rubble", t.parent, p, chunk, GameMaterials.ColumnColor);
                    go.transform.rotation = t.rotation;
                }
                var rb = PrefabPlacement.EnsureBody(go, mass, continuous: false);
                rb.isKinematic = false;
                rb.angularVelocity = Random.insideUnitSphere * 2f;
                Fragment.Mark(go, column); // iki moloz parçası tek kolon sayılır
                if (Floor != null) Floor.Bodies.Add(rb);
            }

            column.gameObject.SetActive(false);
            Destroy(column.gameObject);
        }

        /// <param name="prefab">Özel efekt prefabı; boşsa kodla kurulan <see cref="Vfx.Blast"/> oynar.</param>
        private void SpawnVfx(GameObject prefab, Vector3 pos)
        {
            AudioDirector.Play(Sound.Explosion, pos);
            var light = new GameObject("BlastLight").AddComponent<Light>();
            light.transform.position = pos;
            light.type = LightType.Point;
            light.color = new Color(1f, 0.65f, 0.3f);
            light.range = 9f;
            light.shadows = LightShadows.None;
            light.gameObject.AddComponent<LightFlash>();

            if (prefab != null)
            {
                var vfx = Instantiate(prefab, pos, Quaternion.identity);
                Destroy(vfx, 4f);
                return;
            }

            if (Floor != null) Vfx.Blast(pos, Floor.transform, Floor.BaseY);
            else Vfx.Blast(pos, null, 0f);
        }

        /// <summary>Kısa turuncu ışık parlaması; scaled zamanla söner, ağır çekimde uzar.</summary>
        private class LightFlash : MonoBehaviour
        {
            private const float Duration = 0.4f;
            private const float Peak = 8f;
            private Light _light;
            private float _t;

            private void Awake() => _light = GetComponent<Light>();

            private void Update()
            {
                _t += Time.deltaTime;
                float k = 1f - _t / Duration;
                _light.intensity = Peak * k * k;
                if (_t >= Duration) Destroy(gameObject);
            }
        }
    }
}
