using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Büyük eşyaların altında, durumunu gösteren zemin halkası: bağlıysa yeşil, bağsızsa turuncu, güvenli bölgenin
    /// dışındaysa yanıp sönen kırmızı. Patlama sırasında bölgeden çıkan her cisim için bant o noktada kırmızı yanar;
    /// eşyalar için ayrıca "−1 CAN" / "+2 ceza" yazısı yükselir, geri dönerse "kurtarıldı" yazar.
    /// </summary>
    public class ItemIndicators : MonoBehaviour
    {
        private const int Segments = 40;
        private const float RingWidth = 0.07f;
        private const float RingPadding = 0.15f;
        private const float GroundProbe = 6f;
        private const float WarnCooldown = 1.2f;

        private static readonly Color TiedColor = new Color(0.35f, 1f, 0.45f, 0.9f);
        private static readonly Color UntiedColor = new Color(1f, 0.72f, 0.2f, 0.9f);
        private static readonly Color OutColor = new Color(1f, 0.22f, 0.18f, 1f);
        private static readonly Color PenaltyColor = new Color(1f, 0.6f, 0.2f);
        private static readonly Color SavedColor = new Color(0.45f, 1f, 0.5f);

        private GameManager _gm;
        private Floor _floor;
        private readonly Dictionary<Item, LineRenderer> _rings = new Dictionary<Item, LineRenderer>();
        private readonly Dictionary<Rigidbody, Item> _itemOf = new Dictionary<Rigidbody, Item>();
        private readonly Dictionary<Rigidbody, bool> _inside = new Dictionary<Rigidbody, bool>();
        private readonly Dictionary<Rigidbody, float> _lastWarn = new Dictionary<Rigidbody, float>();
        private readonly RaycastHit[] _hits = new RaycastHit[16];

        public void Init(GameManager gm) => _gm = gm;

        private void LateUpdate()
        {
            if (_gm == null) return;
            var floor = _gm.ActiveFloor;
            if (floor != _floor) Reset(floor);
            if (floor == null) return;

            var state = _gm.State;
            bool tracking = state == GameState.Detonating || state == GameState.Settling;
            if (tracking) TrackZone(floor);
            else
            {
                _inside.Clear();
                _lastWarn.Clear();
            }

            UpdateRings(state);
        }

        private void Reset(Floor floor)
        {
            foreach (var ring in _rings.Values)
            {
                if (ring != null) Destroy(ring.gameObject);
            }
            _rings.Clear();
            _itemOf.Clear();
            _inside.Clear();
            _lastWarn.Clear();
            _floor = floor;
            if (floor == null) return;

            foreach (var item in floor.Items)
            {
                if (item == null) continue;
                _itemOf[item.Body] = item;
                if (item.Size == ItemSize.Big) _rings[item] = CreateRing(item);
            }
        }

        #region Bölge takibi

        private void TrackZone(Floor floor)
        {
            var zone = _gm.Zone;
            foreach (var rb in floor.Bodies)
            {
                if (rb == null || !rb.gameObject.activeInHierarchy || rb.isKinematic || floor.ItemParts.Contains(rb)) continue;

                Vector3 p = rb.worldCenterOfMass;
                bool now = zone.Contains(p);
                if (!_inside.TryGetValue(rb, out bool was))
                {
                    _inside[rb] = now;
                    continue;
                }
                if (was == now) continue;
                _inside[rb] = now;

                if (_lastWarn.TryGetValue(rb, out float last) && Time.unscaledTime - last < WarnCooldown) continue;
                _lastWarn[rb] = Time.unscaledTime;

                _itemOf.TryGetValue(rb, out var item);
                if (!now)
                {
                    zone.Flash(p);
                    if (item == null) continue;
                    bool big = item.Size == ItemSize.Big;
                    GameHUD.Float(p + Vector3.up, big ? "−1 CAN" : $"+{FloorScorer.SmallItemPenalty} ceza",
                        big ? OutColor : PenaltyColor);
                }
                else if (item != null)
                {
                    GameHUD.Float(p + Vector3.up, "kurtarıldı", SavedColor);
                }
            }
        }

        #endregion

        #region Halkalar

        private LineRenderer CreateRing(Item item)
        {
            var go = new GameObject($"Ring_{item.DisplayName}");
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = true;
            line.positionCount = Segments;
            line.widthMultiplier = RingWidth;
            line.numCornerVertices = 2;
            line.alignment = LineAlignment.TransformZ; // zemine yatık dursun
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = Vfx.LineMaterial;

            float radius = (item.Definition != null ? item.Definition.ResolveFootprintRadius() : 0.5f) + RingPadding;
            for (int i = 0; i < Segments; i++)
            {
                float a = i / (float)Segments * Mathf.PI * 2f;
                line.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f));
            }
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            return line;
        }

        private void UpdateRings(GameState state)
        {
            bool show = state != GameState.GameOver;
            bool judging = state == GameState.Detonating || state == GameState.Settling || state == GameState.Results;
            float pulse = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 10f);

            foreach (var pair in _rings)
            {
                var item = pair.Key;
                var line = pair.Value;
                if (line == null) continue;
                if (!show || item == null || !item.gameObject.activeInHierarchy || !TryGround(item, out Vector3 ground))
                {
                    line.enabled = false;
                    continue;
                }

                line.enabled = true;
                line.transform.position = ground + Vector3.up * 0.03f;

                Color c;
                if (judging && !_gm.Zone.Contains(item.Body.worldCenterOfMass))
                {
                    c = OutColor;
                    c.a *= pulse;
                }
                else c = item.IsTied ? TiedColor : UntiedColor;
                line.startColor = line.endColor = c;
            }
        }

        /// <summary>Eşyanın altındaki ilk yüzey (eşyanın kendi collider'ları hariç).</summary>
        private bool TryGround(Item item, out Vector3 point)
        {
            Vector3 origin = item.Body.worldCenterOfMass;
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, GroundProbe, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            point = default;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider.attachedRigidbody == item.Body || h.distance >= best) continue;
                best = h.distance;
                point = h.point;
            }
            return best < float.MaxValue;
        }

        #endregion
    }
}
