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

        public Rect Area { get; private set; }
        public float RoofY { get; private set; }

        private readonly List<GameObject> _tape = new List<GameObject>();

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
                    var seg = GameMaterials.CreateBox("Tape", transform, center, size,
                        index++ % 2 == 0 ? GameMaterials.TapeYellow : GameMaterials.TapeBlack, false);
                    _tape.Add(seg);
                }
            }
        }
    }
}
