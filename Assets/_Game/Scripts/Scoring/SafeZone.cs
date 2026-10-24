using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Alt katın tavanı + pay. Enkaz ve eşyalar bunun içinde kalmalı.
    /// Çevresi sarı-siyah şeritle çizilir.
    /// </summary>
    public class SafeZone : MonoBehaviour
    {
        private const float SegmentLength = 0.5f;
        private const float BelowRoofTolerance = 0.75f;
        private const float FlashDuration = 0.6f;
        private const float FlashRadius = 1.6f;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly Color FlashColor = new Color(1f, 0.15f, 0.1f);

        public Rect Area { get; private set; }
        public float RoofY { get; private set; }

        private readonly List<GameObject> _tape = new List<GameObject>();
        private readonly List<Color> _tapeColors = new List<Color>();
        private readonly List<float> _flash = new List<float>();
        private MaterialPropertyBlock _block;

        /// <summary>Bölgeden bir şey çıktığında o noktaya yakın bant parçaları kısa süre kırmızı yanar.</summary>
        public void Flash(Vector3 point)
        {
            for (int i = 0; i < _tape.Count; i++)
            {
                var seg = _tape[i];
                if (seg == null) continue;
                Vector3 d = seg.transform.position - point;
                d.y = 0f;
                if (d.magnitude < FlashRadius) _flash[i] = FlashDuration;
            }
        }

        private void Update()
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < _tape.Count; i++)
            {
                if (_flash[i] <= 0f) continue;
                _flash[i] = Mathf.Max(0f, _flash[i] - dt);
                var r = _tape[i] != null ? _tape[i].GetComponent<Renderer>() : null;
                if (r == null) continue;
                float k = _flash[i] / FlashDuration;
                r.GetPropertyBlock(_block);
                _block.SetColor(BaseColorId, Color.Lerp(_tapeColors[i], FlashColor, k));
                r.SetPropertyBlock(_block);
            }
        }

        public void Configure(Rect footprint, float roofY, float margin)
        {
            Area = new Rect(footprint.xMin - margin, footprint.yMin - margin,
                footprint.width + margin * 2f, footprint.height + margin * 2f);
            RoofY = roofY;
            BuildTape();
        }

        public bool Contains(Vector3 p)
        {
            return p.y > RoofY - BelowRoofTolerance && Area.Contains(new Vector2(p.x, p.z));
        }

        private void BuildTape()
        {
            foreach (var t in _tape) Destroy(t);
            _tape.Clear();
            _tapeColors.Clear();
            _flash.Clear();

            float y = RoofY + 0.03f;
            var a = Area;
            Vector3[] corners =
            {
                new Vector3(a.xMin, y, a.yMin), new Vector3(a.xMax, y, a.yMin),
                new Vector3(a.xMax, y, a.yMax), new Vector3(a.xMin, y, a.yMax)
            };

            int index = 0;
            for (int side = 0; side < 4; side++)
            {
                Vector3 from = corners[side], to = corners[(side + 1) % 4];
                float len = Vector3.Distance(from, to);
                int count = Mathf.Max(1, Mathf.RoundToInt(len / SegmentLength));
                Vector3 dir = (to - from) / count;
                for (int i = 0; i < count; i++)
                {
                    Vector3 center = from + dir * (i + 0.5f);
                    bool alongX = Mathf.Abs(dir.x) > Mathf.Abs(dir.z);
                    Vector3 size = alongX
                        ? new Vector3(dir.magnitude, 0.04f, 0.12f)
                        : new Vector3(0.12f, 0.04f, dir.magnitude);
                    var color = index++ % 2 == 0 ? GameMaterials.TapeYellow : GameMaterials.TapeBlack;
                    var seg = GameMaterials.CreateBox("Tape", transform, center, size, color, false);
                    _tape.Add(seg);
                    _tapeColors.Add(color);
                    _flash.Add(0f);
                }
            }
        }
    }
}
