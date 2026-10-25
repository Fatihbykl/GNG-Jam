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

        // Ok ölçüleri (dünya birimi). Gövde kalınlığı küçük tutulur: üstten bakınca düz, yandan hafif hacimli.
        private const float ShaftHalfWidth = 0.2f;
        private const float HeadHalfWidth = 0.55f;
        private const float HeadLength = 0.85f;
        private const float Thickness = 0.1f;
        private const float OutlineWidth = 0.07f;
        private const float TailOffset = 0.6f;
        private const float FollowSpeed = 12f;
        private const float PulseAmplitude = 0.12f;
        private const float PulseSpeed = 3f;

        private static readonly Color OutlineColor = new Color(0.22f, 0.1f, 0.02f);

        public bool HasDirection { get; private set; }
        public Vector3 Direction { get; private set; }
        public Vector3 Tip { get; private set; }
        public Vector3 Center { get; private set; }
        public float Strength { get; private set; }

        private GameManager _gm;
        private Transform _arrow;
        private Mesh _bodyMesh;
        private Mesh _outlineMesh;
        private float _meshLength = -1f;
        private Vector3 _shownDir;
        private float _shownLength;
        private readonly List<(Vector2 offset, float fireTime)> _charged = new List<(Vector2, float)>();
        private readonly List<Vector2> _bearing = new List<Vector2>();

        public void Init(GameManager gm)
        {
            _gm = gm;
            _arrow = new GameObject("CollapseArrow").transform;
            _arrow.SetParent(transform, false);
            _bodyMesh = new Mesh { name = "CollapseArrowBody" };
            _outlineMesh = new Mesh { name = "CollapseArrowOutline" };
            AddPart("Body", _bodyMesh, GameMaterials.CollapseArrowColor, 0f);
            // Koyu kontur gövdenin biraz altında: ok zemin ve eşyalar üzerinde net ayrışır
            AddPart("Outline", _outlineMesh, OutlineColor, -Thickness * 0.35f);
            _arrow.gameObject.SetActive(false);
        }

        private void AddPart(string name, Mesh mesh, Color color, float yOffset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_arrow, false);
            go.transform.localPosition = new Vector3(0f, yOffset, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GameMaterials.Get(color);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
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
                _arrow.gameObject.SetActive(false);
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
                _arrow.gameObject.SetActive(false);
                _shownLength = 0f;
                return;
            }

            Direction = new Vector3(dir.x, 0f, dir.y);
            float halfExtent = Mathf.Min(floor.Footprint.width, floor.Footprint.height) * 0.5f;
            float length = Mathf.Lerp(1.2f, halfExtent + 1f, Mathf.Clamp01(strength * 2f));

            // Sıra değişince ok zıplamasın, yumuşakça dönüp uzasın
            float k = 1f - Mathf.Exp(-FollowSpeed * Time.unscaledDeltaTime);
            bool fresh = !_arrow.gameObject.activeSelf || _shownLength <= 0f;
            _shownDir = fresh ? Direction : Vector3.Slerp(_shownDir, Direction, k).normalized;
            _shownLength = fresh ? length : Mathf.Lerp(_shownLength, length, k);
            Tip = Center + _shownDir * _shownLength;

            float total = _shownLength + TailOffset;
            if (Mathf.Abs(total - _meshLength) > 0.01f)
            {
                _meshLength = total;
                BuildArrow(_bodyMesh, total, ShaftHalfWidth, HeadHalfWidth, HeadLength, 0f);
                BuildArrow(_outlineMesh, total, ShaftHalfWidth + OutlineWidth, HeadHalfWidth + OutlineWidth * 2f,
                    HeadLength + OutlineWidth, OutlineWidth);
            }

            // Ok yönünde hafifçe ileri geri nabız atar
            float pulse = (Mathf.Sin(Time.unscaledTime * PulseSpeed) * 0.5f + 0.5f) * PulseAmplitude;
            _arrow.SetPositionAndRotation(Center - _shownDir * (TailOffset - pulse),
                Quaternion.LookRotation(_shownDir, Vector3.up));
            _arrow.gameObject.SetActive(true);
        }

        /// <summary>
        /// Yerel +Z yönüne bakan, kalınlığı olan düz ok: dikdörtgen gövde + üçgen uç. Her yüzün kendi köşeleri var,
        /// böylece low-poly düz gölgelenir.
        /// </summary>
        private static void BuildArrow(Mesh mesh, float length, float shaft, float head, float headLength, float tailExtend)
        {
            float neck = Mathf.Max(0.1f, length - headLength);
            float tip = length + tailExtend * 0.5f;
            var outline = new[]
            {
                new Vector2(-shaft, -tailExtend), new Vector2(shaft, -tailExtend), new Vector2(shaft, neck),
                new Vector2(head, neck), new Vector2(0f, tip), new Vector2(-head, neck), new Vector2(-shaft, neck)
            };
            float top = Thickness * 0.5f, bottom = -Thickness * 0.5f;

            var verts = new List<Vector3>();
            var tris = new List<int>();

            void Face(params Vector3[] poly)
            {
                int start = verts.Count;
                verts.AddRange(poly);
                for (int i = 1; i < poly.Length - 1; i++)
                {
                    tris.Add(start);
                    tris.Add(start + i);
                    tris.Add(start + i + 1);
                }
            }

            Vector3 Top(int i) => new Vector3(outline[i].x, top, outline[i].y);
            Vector3 Bottom(int i) => new Vector3(outline[i].x, bottom, outline[i].y);

            // Üst yüz (saat yönü, yukarıdan bakınca görünür): gövde + uç
            Face(Top(0), Top(6), Top(2), Top(1));
            Face(Top(5), Top(4), Top(3));
            // Alt yüz
            Face(Bottom(0), Bottom(1), Bottom(2), Bottom(6));
            Face(Bottom(5), Bottom(3), Bottom(4));
            // Yan yüzler
            for (int i = 0; i < outline.Length; i++)
            {
                int n = (i + 1) % outline.Length;
                Face(Top(i), Top(n), Bottom(n), Bottom(i));
            }

            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
