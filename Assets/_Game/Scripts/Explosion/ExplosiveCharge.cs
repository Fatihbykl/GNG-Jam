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
                    sum += c.transform.position;
                    n++;
                }
                return n > 0 ? sum / n + Vector3.up * 1.2f : transform.position;
            }
        }

        public void AddColumn(StructuralPiece column)
        {
            Columns.Add(column);
            var marker = GameMaterials.CreateBox("Charge", column.transform, column.transform.position, Vector3.one,
                GameMaterials.ChargeColor, false);
            var s = column.transform.lossyScale;
            marker.transform.localScale = new Vector3(0.5f / s.x, 0.35f / s.y, 0.5f / s.z);
            marker.transform.localPosition = new Vector3(0f, -0.25f, 0f);
            _markers.Add(marker);
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
                Vector3 pos = column.transform.position;
                SpawnVfx(vfxPrefab, pos);
                Cut(column);

                hit.Clear();
                foreach (var col in Physics.OverlapSphere(pos, config.explosionRadius, ~0, QueryTriggerInteraction.Ignore))
                {
                    var rb = col.attachedRigidbody;
                    if (rb == null || rb.isKinematic || !hit.Add(rb)) continue;
                    rb.AddExplosionForce(config.explosionKick, pos, config.explosionRadius, config.explosionUpward,
                        ForceMode.VelocityChange);
                }

                foreach (var rope in RopeController.AllRopes)
                {
                    rope.AddExplosionForce(config.ropeBlastPower, pos, config.explosionRadius);
                }
            }
        }

        /// <summary>
        /// Kolonu keser: tüm eklemleri ve kolonun kendisi gider, yerine iki moloz parçası kalır.
        /// Kolon objesiyle birlikte üstündeki ankraj da yok olur.
        /// </summary>
        private void Cut(StructuralPiece column)
        {
            column.DetachAll();

            var t = column.transform;
            Vector3 size = t.lossyScale;
            float mass = column.Body.mass * RubbleMassShare;
            Vector3 chunk = new Vector3(size.x * 0.9f, size.y * 0.45f, size.z * 0.9f);

            for (int i = 0; i < 2; i++)
            {
                Vector3 p = t.position + Vector3.up * (i == 0 ? -size.y * 0.25f : size.y * 0.25f);
                var go = GameMaterials.CreateBox("Rubble", t.parent, p, chunk, GameMaterials.ColumnColor);
                go.transform.rotation = t.rotation;
                var rb = GameMaterials.AddBody(go, mass);
                rb.isKinematic = false;
                rb.angularVelocity = Random.insideUnitSphere * 2f;
                if (Floor != null) Floor.Bodies.Add(rb);
            }

            column.gameObject.SetActive(false);
            Destroy(column.gameObject);
        }

        private static void SpawnVfx(GameObject prefab, Vector3 pos)
        {
            if (prefab != null)
            {
                var vfx = Instantiate(prefab, pos, Quaternion.identity);
                Destroy(vfx, 4f);
                return;
            }

            // Prefab yoksa basit bir parlama
            var flash = GameMaterials.CreateBox("Flash", null, pos, Vector3.one * 0.8f, new Color(1f, 0.6f, 0.1f), false);
            flash.AddComponent<FlashFade>();
        }

        private class FlashFade : MonoBehaviour
        {
            private float _t;

            private void Update()
            {
                _t += Time.deltaTime;
                transform.localScale = Vector3.one * Mathf.Lerp(0.8f, 3f, _t / 0.35f);
                if (_t > 0.35f) Destroy(gameObject);
            }
        }
    }
}
