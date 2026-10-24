using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Komşu binanın düz kutusuna pencere sıraları ve kat bantları giydirir. Bina sonsuz uzun olduğundan cephe
    /// yalnızca aktif katın çevresinde birkaç kat boyunca kurulur ve kat indikçe aşağı kaydırılır.
    /// Collider'ı yoktur, sadece görseldir.
    /// </summary>
    public class NeighborFacade : MonoBehaviour
    {
        private const int RowsAbove = 3;
        private const int RowsBelow = 6;
        private const float WindowSpacing = 1.6f;
        private const float WindowWidth = 0.9f;
        private const float WindowHeight = 1.3f;
        private const float WindowSill = 0.9f;
        private const float Protrude = 0.04f;
        private const float BandHeight = 0.18f;
        private const float LitChance = 0.18f;

        private static readonly Color GlassColor = new Color(0.22f, 0.29f, 0.4f);
        private static readonly Color LitColor = new Color(1f, 0.86f, 0.58f);
        private static readonly Color BandColor = new Color(0.58f, 0.66f, 0.78f);

        private readonly List<GameObject> _rows = new List<GameObject>();
        private float _topY;
        private float _floorHeight;

        /// <param name="center">Binanın taban merkezi (xz) ve tepe yüksekliği (y).</param>
        /// <param name="size">Binanın genişliği (x) ve derinliği (z).</param>
        public static NeighborFacade Create(Transform parent, Vector3 center, Vector3 size, float floorHeight, int seed)
        {
            var go = new GameObject("NeighborFacade");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(center.x, 0f, center.z);
            var facade = go.AddComponent<NeighborFacade>();
            facade._topY = center.y;
            facade._floorHeight = floorHeight;
            facade.Build(size, new System.Random(seed));
            return facade;
        }

        private void Build(Vector3 size, System.Random rng)
        {
            float hx = size.x * 0.5f, hz = size.z * 0.5f;
            for (int k = -RowsBelow; k <= RowsAbove; k++)
            {
                var row = new GameObject($"Row_{k}");
                row.transform.SetParent(transform, false);
                row.transform.localPosition = new Vector3(0f, k * _floorHeight, 0f);
                _rows.Add(row);

                // Kat bandı: binayı saran ince çıkıntı
                float bandY = BandHeight * 0.5f;
                Box(row, new Vector3(0f, bandY, hz + Protrude), new Vector3(size.x + Protrude * 2f, BandHeight, Protrude * 2f), BandColor);
                Box(row, new Vector3(0f, bandY, -hz - Protrude), new Vector3(size.x + Protrude * 2f, BandHeight, Protrude * 2f), BandColor);
                Box(row, new Vector3(hx + Protrude, bandY, 0f), new Vector3(Protrude * 2f, BandHeight, size.z), BandColor);
                Box(row, new Vector3(-hx - Protrude, bandY, 0f), new Vector3(Protrude * 2f, BandHeight, size.z), BandColor);

                float wy = WindowSill + WindowHeight * 0.5f;
                Windows(row, rng, size.x, p => new Vector3(p, wy, hz + Protrude), new Vector3(WindowWidth, WindowHeight, Protrude * 2f));
                Windows(row, rng, size.x, p => new Vector3(p, wy, -hz - Protrude), new Vector3(WindowWidth, WindowHeight, Protrude * 2f));
                Windows(row, rng, size.z, p => new Vector3(hx + Protrude, wy, p), new Vector3(Protrude * 2f, WindowHeight, WindowWidth));
                Windows(row, rng, size.z, p => new Vector3(-hx - Protrude, wy, p), new Vector3(Protrude * 2f, WindowHeight, WindowWidth));
            }
        }

        private static void Windows(GameObject row, System.Random rng, float length, System.Func<float, Vector3> place, Vector3 size)
        {
            int count = Mathf.FloorToInt((length - 0.6f) / WindowSpacing);
            if (count <= 0) return;
            float start = -(count - 1) * WindowSpacing * 0.5f;
            for (int i = 0; i < count; i++)
            {
                var color = rng.NextDouble() < LitChance ? LitColor : GlassColor;
                Box(row, place(start + i * WindowSpacing), size, color);
            }
        }

        private static void Box(GameObject row, Vector3 localPos, Vector3 size, Color color)
        {
            var box = GameMaterials.CreateBox("F", row.transform, Vector3.zero, size, color, false);
            box.transform.localPosition = localPos;
            box.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>Cepheyi aktif katın zemin yüksekliğine hizalar; binanın tepesinin üstündeki sıraları gizler.</summary>
        public void Follow(float floorBaseY)
        {
            var p = transform.position;
            transform.position = new Vector3(p.x, floorBaseY, p.z);
            foreach (var row in _rows)
            {
                float y = row.transform.position.y;
                row.SetActive(y + WindowSill + WindowHeight < _topY - 0.3f);
            }
        }
    }
}
