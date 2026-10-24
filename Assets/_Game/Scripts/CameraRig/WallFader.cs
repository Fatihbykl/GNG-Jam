using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Bebek evi kesiti. Planlamada kameraya bakan dış duvarları gizler (gölgeleri kalır; tavanı GameManager gizler).
    /// Patlama ve sonrasında göz hizasının üstündeki tavan parçaları ve kameraya bakan ayakta duvarlar saydamlaşır,
    /// böylece ipler ve eşyalar görünür kalır. Yere inen enkaz yeniden opaklaşır. Cell Fracture parçaları da
    /// kendi yüksekliklerine göre tek tek solar.
    /// </summary>
    public class WallFader : MonoBehaviour
    {
        private const float FacingThreshold = 0.15f;
        private const float OpaqueThreshold = 0.995f;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");

        [Range(0f, 1f)] public float CeilingAlpha = 0.12f;
        [Range(0f, 1f)] public float WallAlpha = 0.2f;
        [Tooltip("Kat zemininden bu yüksekliğin (m) üstündeki tavan parçaları saydamlaşır.")]
        public float CeilingFadeHeight = 1.2f;
        [Tooltip("Kameraya bakan duvar parçaları bu yüksekliğin (m) üstündeyse saydamlaşır.")]
        public float WallFadeHeight = 0.8f;
        [Tooltip("Saniyedeki saydamlık değişimi (gerçek zaman, ağır çekimden etkilenmez).")]
        public float FadeSpeed = 5f;

        private class FadeState
        {
            public Material[] Original;
            public Material[] Faded;
            public Color[] Colors;
            public float Alpha = 1f;
            public bool Applied;
        }

        private static readonly Dictionary<Material, Material> FadedCache = new Dictionary<Material, Material>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => FadedCache.Clear();

        private GameManager _gm;
        private Floor _floor;
        private MaterialPropertyBlock _block;
        private readonly Dictionary<Renderer, FadeState> _states = new Dictionary<Renderer, FadeState>();

        public void Init(GameManager gm) => _gm = gm;

        private void LateUpdate()
        {
            if (_gm == null || _gm.ActiveFloor == null) return;
            if (_block == null) _block = new MaterialPropertyBlock();

            var floor = _gm.ActiveFloor;
            if (floor != _floor)
            {
                _states.Clear(); // eski katın renderer'ları yok edildi
                _floor = floor;
            }

            bool planning = _gm.State == GameState.Planning;
            Vector3 camPos = transform.position;
            float step = FadeSpeed * Time.unscaledDeltaTime;

            foreach (var wall in floor.Walls)
            {
                if (wall == null) continue;
                bool facing = IsFacing(wall, camPos);
                wall.SetCutaway(planning && facing);
                UpdatePiece(wall, planning || !facing ? 1f : WallAlpha, floor.BaseY + WallFadeHeight, step);
            }

            foreach (var tile in floor.CeilingTiles)
            {
                if (tile == null) continue;
                UpdatePiece(tile, planning ? 1f : CeilingAlpha, floor.BaseY + CeilingFadeHeight, step);
            }
        }

        private static bool IsFacing(StructuralPiece wall, Vector3 camPos)
        {
            Vector3 toCam = camPos - wall.transform.position;
            toCam.y = 0f;
            return Vector3.Dot(wall.WorldOutward, toCam.normalized) > FacingThreshold;
        }

        /// <param name="fadedAlpha">Eşiğin üstündeki renderer'ların hedef saydamlığı (1 = hiç solmaz).</param>
        /// <param name="minY">Bu yüksekliğin altına inen renderer opak kalır.</param>
        private void UpdatePiece(StructuralPiece piece, float fadedAlpha, float minY, float step)
        {
            bool anyFaded = false;
            foreach (var r in piece.Renderers)
            {
                if (r == null) continue;
                float target = fadedAlpha < 1f && r.bounds.center.y > minY ? fadedAlpha : 1f;
                if (!_states.TryGetValue(r, out var state))
                {
                    if (target >= 1f) continue; // hiç solmamış renderer için durum tutma
                    state = CreateState(r);
                    _states[r] = state;
                }

                state.Alpha = Mathf.MoveTowards(state.Alpha, target, step);
                Apply(r, state);
                if (state.Alpha < 0.6f) anyFaded = true;
            }
            piece.IsFaded = anyFaded;
        }

        private FadeState CreateState(Renderer r)
        {
            var original = r.sharedMaterials;
            var state = new FadeState
            {
                Original = original,
                Faded = new Material[original.Length],
                Colors = new Color[original.Length]
            };
            for (int i = 0; i < original.Length; i++)
            {
                var m = original[i];
                state.Faded[i] = GetFaded(m);
                state.Colors[i] = m != null && m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : Color.white;
            }
            return state;
        }

        private void Apply(Renderer r, FadeState state)
        {
            if (state.Alpha >= OpaqueThreshold)
            {
                if (!state.Applied) return;
                r.sharedMaterials = state.Original;
                _block.Clear();
                for (int i = 0; i < state.Original.Length; i++) r.SetPropertyBlock(_block, i);
                state.Applied = false;
                return;
            }

            if (!state.Applied)
            {
                r.sharedMaterials = state.Faded;
                state.Applied = true;
            }
            for (int i = 0; i < state.Faded.Length; i++)
            {
                Color c = state.Colors[i];
                c.a *= state.Alpha;
                r.GetPropertyBlock(_block, i);
                _block.SetColor(BaseColorId, c);
                r.SetPropertyBlock(_block, i);
            }
        }

        /// <summary>Kaynak materyalin saydam kopyası: şablondan üretilir, rengi ve dokusu kaynaktan gelir.</summary>
        private Material GetFaded(Material source)
        {
            if (source == null) return null;
            if (FadedCache.TryGetValue(source, out var faded) && faded != null) return faded;

            var template = _gm.Content != null ? _gm.Content.FadeMaterial : null;
            if (template != null)
            {
                faded = new Material(template);
                if (source.HasProperty(BaseMapId) && faded.HasProperty(BaseMapId))
                {
                    faded.SetTexture(BaseMapId, source.GetTexture(BaseMapId));
                    faded.SetTextureScale(BaseMapId, source.GetTextureScale(BaseMapId));
                    faded.SetTextureOffset(BaseMapId, source.GetTextureOffset(BaseMapId));
                }
            }
            else
            {
                faded = new Material(source);
                MakeTransparent(faded);
            }

            faded.name = source.name + "_Fade";
            FadedCache[source] = faded;
            return faded;
        }

        /// <summary>Şablon yoksa URP Lit materyalini çalışma anında saydam yapar.</summary>
        private static void MakeTransparent(Material m)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", 1f);
            m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetShaderPassEnabled("DepthOnly", false);
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
    }
}
