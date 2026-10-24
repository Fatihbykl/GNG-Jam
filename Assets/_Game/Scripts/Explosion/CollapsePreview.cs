using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace KatKat
{
    /// <summary>
    /// Planlamada patlama sırasından tahmini çöküş yönünü ok olarak gösterir. Bina, desteğini erken kaybeden
    /// tarafa doğru yatar ve ayakta kalan taşıyıcı kolonlardan uzaklaşır. Fizik deterministik değil;
    /// ok yalnızca yaklaşık bir gösterim.
    /// </summary>
    public class CollapsePreview : MonoBehaviour
    {
        private const float FireTimeFalloff = 0.8f;
        private const float BearingWeight = 0.6f;
        private const float MinStrength = 0.08f;

        public bool HasDirection { get; private set; }
        public Vector3 Direction { get; private set; }
        public Vector3 Tip { get; private set; }
        public Vector3 Center { get; private set; }
        public float Strength { get; private set; }

        private GameManager _gm;
        private LineRenderer _line;
        private readonly List<(Vector2 offset, float fireTime)> _charged = new List<(Vector2, float)>();
        private readonly List<Vector2> _bearing = new List<Vector2>();

        public void Init(GameManager gm)
        {
            _gm = gm;
            var go = new GameObject("CollapseArrow");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();
            _line.positionCount = 5;
            _line.widthMultiplier = 0.14f;
            _line.numCornerVertices = 2;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.sharedMaterial = GameMaterials.Get(GameMaterials.CollapseArrowColor);
            _line.enabled = false;
        }

        /// <summary>
        /// Kolonların kat merkezine göre XZ ofsetlerinden çöküş yönünü tahmin eder.
        /// Erken patlayan kolonlar daha ağır basar; taşıyıcı kolonlar yönü kendilerinden uzağa iter.
        /// </summary>
        /// <param name="strength">0 = simetrik (merkeze çöker), 1 = tamamen tek yöne.</param>
        public static Vector2 Estimate(IList<(Vector2 offset, float fireTime)> charged, IList<Vector2> bearing,
            out float strength)
        {
            Vector2 sum = Vector2.zero;
            float norm = 0f;
            foreach (var (offset, fireTime) in charged)
            {
                float w = Mathf.Exp(-Mathf.Max(0f, fireTime) / FireTimeFalloff);
                sum += offset * w;
                norm += offset.magnitude * w;
            }
            foreach (var offset in bearing)
            {
                sum -= offset * BearingWeight;
                norm += offset.magnitude * BearingWeight;
            }

            strength = norm > 1e-4f ? Mathf.Clamp01(sum.magnitude / norm) : 0f;
            return sum.sqrMagnitude > 1e-8f ? sum.normalized : Vector2.zero;
        }

        private void LateUpdate()
        {
            var floor = _gm.ActiveFloor;
            if (_gm.State != GameState.Planning || floor == null)
            {
                HasDirection = false;
                _line.enabled = false;
                return;
            }

            Vector2 center = floor.Footprint.center;
            _charged.Clear();
            _bearing.Clear();
            foreach (var column in floor.Columns)
            {
                if (column == null) continue;
                var p = column.WorldCenter;
                var offset = new Vector2(p.x - center.x, p.z - center.y);
                if (column.IsBearing) _bearing.Add(offset);
                else if (column.Charge != null) _charged.Add((offset, _gm.Sequence.FireTimeOf(column.Charge)));
            }

            Vector2 dir = Estimate(_charged, _bearing, out float strength);
            Strength = strength;
            float y = floor.BaseY + _gm.Config.floorHeight - _gm.Config.slabThickness - 0.25f;
            Center = new Vector3(center.x, y, center.y);
            HasDirection = strength >= MinStrength;
            if (!HasDirection)
            {
                _line.enabled = false;
                return;
            }

            Direction = new Vector3(dir.x, 0f, dir.y);
            float halfExtent = Mathf.Min(floor.Footprint.width, floor.Footprint.height) * 0.5f;
            float length = Mathf.Lerp(1.2f, halfExtent + 1f, Mathf.Clamp01(strength * 2f));
            Vector3 tail = Center - Direction * 0.6f;
            Tip = Center + Direction * length;
            Vector3 side = Vector3.Cross(Vector3.up, Direction) * 0.45f;
            Vector3 back = Tip - Direction * 0.7f;

            _line.SetPosition(0, tail);
            _line.SetPosition(1, Tip);
            _line.SetPosition(2, back + side);
            _line.SetPosition(3, Tip);
            _line.SetPosition(4, back - side);
            _line.enabled = true;
        }
    }
}
