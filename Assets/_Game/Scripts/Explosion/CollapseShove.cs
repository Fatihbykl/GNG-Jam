using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Gerçek bir çöküşte kat, desteğini erken kaybeden tarafa yatar ve eşyalar o yöne kayar. Bizde eşyaların
    /// üstünde durduğu zemin (alt katın tavanı) sabit olduğundan bunu kurala bağlı bir itki olarak uygularız:
    /// her şarj, kolonlarının yakınındaki eşyalara kısa bir itki dalgası verir (<see cref="CollapseImpulse"/>).
    /// Aynı kurallar planlamada tahmin edildiği için oyuncu sonucu önceden görür.
    /// Duvarlar da burada devrilir: bina hangi tarafa yatıyorsa o taraftakiler dışa, diğerleri içe.
    /// </summary>
    public class CollapseShove : MonoBehaviour
    {
        private Floor _floor;
        private GameConfig _cfg;
        private int _floorNumber;
        private CollapseImpulse.PushContext _ctx;
        private float _start;
        private readonly List<CollapseImpulse.Wave> _waves = new List<CollapseImpulse.Wave>();

        public bool IsActive => _floor != null;

        /// <param name="direction">Yatay çöküş yönü; sıfırsa simetrik çöküş.</param>
        /// <param name="strength">0 = simetrik, 1 = tamamen tek yöne (bkz. <see cref="CollapsePreview.Strength"/>).</param>
        public void Arm(Floor floor, Vector3 direction, float strength, int floorNumber, GameConfig cfg)
        {
            _floor = floor;
            _cfg = cfg;
            _floorNumber = floorNumber;
            _ctx = CollapseImpulse.PushContext.For(floor.Footprint, direction, strength, cfg);
            _start = Time.time;
            _waves.Clear();
        }

        /// <summary>Şarj patlamadan hemen önce (kolonlar yerindeyken) çağrılır.</summary>
        public void OnChargeFired(ExplosiveCharge charge)
        {
            if (_floor == null || charge == null) return;

            var columns = new List<Vector2>();
            foreach (var c in charge.Columns)
            {
                if (c != null) columns.Add(CollapseImpulse.Flat(c.WorldCenter));
            }
            _waves.Add(new CollapseImpulse.Wave { Time = Time.time - _start, Columns = columns });
            ToppleWalls(charge);
        }

        public void Stop()
        {
            _floor = null;
            _waves.Clear();
        }

        private void ToppleWalls(ExplosiveCharge charge)
        {
            foreach (var wall in _floor.Walls)
            {
                if (wall == null || wall.FallDirected || !SupportedBy(wall, charge)) continue;
                wall.FallDirected = true;

                Vector2 outward = CollapseImpulse.Flat(wall.WorldOutward).normalized;
                bool outwardFall = CollapseImpulse.WallFallsOutward(outward, _ctx, _cfg);
                var dir = new Vector3(outward.x, 0f, outward.y) * (outwardFall ? 1f : -1f);

                if (wall.TryGetComponent<Fracturable>(out var fracturable))
                {
                    if (!fracturable.IsShattered) fracturable.Shatter(null, 0f, dir * _cfg.wallFallKick);
                    continue;
                }

                var rb = wall.Body;
                if (rb == null || rb.isKinematic) continue;
                wall.DetachAll();
                // Tabanı yerinde, tepesi devrilme yönüne: tepedeki hız ≈ wallFallKick
                float halfHeight = Mathf.Max(0.1f, wall.SlotSize.y * 0.5f);
                rb.linearVelocity = dir * (_cfg.wallFallKick * 0.4f);
                rb.angularVelocity = Vector3.Cross(Vector3.up, dir) * (_cfg.wallFallKick / (halfHeight * 2f));
            }
        }

        private static bool SupportedBy(StructuralPiece wall, ExplosiveCharge charge)
        {
            foreach (var column in wall.SupportColumns)
            {
                if (column != null && column.Charge == charge) return true;
            }
            return false;
        }

        private void FixedUpdate()
        {
            if (_floor == null || _waves.Count == 0) return;

            float t = Time.time - _start;
            foreach (var item in _floor.Items)
            {
                if (item == null) continue;
                var rb = item.Body;
                if (rb == null || rb.isKinematic || !rb.gameObject.activeInHierarchy) continue;
                float baseAccel = CollapseImpulse.PushAccel(item.Size, _floorNumber, _cfg);
                Vector2 a = CollapseImpulse.AccelAt(CollapseImpulse.Flat(rb.worldCenterOfMass), baseAccel, _waves, t, _ctx);
                if (a != Vector2.zero) rb.AddForce(new Vector3(a.x, 0f, a.y), ForceMode.Acceleration);
            }
        }
    }
}
