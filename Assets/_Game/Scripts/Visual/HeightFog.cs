using UnityEngine;
using UnityEngine.Rendering;

namespace KatKat
{
    /// <summary>
    /// Binanın altındaki yükseklik sisi: aktif katın biraz altında, kamerayı XZ'de izleyen büyük yatay düzlem.
    /// Altındaki kule, komşu binalar ve düşen moloz derinleştikçe sisin içinde kaybolur (bkz. KatKat/HeightFog).
    /// Kat inince yüzey yumuşakça aşağı kayar. Değerler <see cref="VisualConfig"/>'ten her kare okunur.
    /// </summary>
    public class HeightFog : MonoBehaviour
    {
        private const float PlaneSize = 1000f;
        private const float FollowSpeed = 3f;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int TopYId = Shader.PropertyToID("_TopY");
        private static readonly int DensityId = Shader.PropertyToID("_Density");
        private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
        private static readonly int NoiseStrengthId = Shader.PropertyToID("_NoiseStrength");
        private static readonly int NoiseSpeedId = Shader.PropertyToID("_NoiseSpeed");

        private GameManager _gm;
        private Camera _cam;
        private Transform _plane;
        private Renderer _renderer;
        private Material _material;
        private float _y;
        private bool _placed;

        public void Init(GameManager gm, Camera cam)
        {
            _gm = gm;
            _cam = cam;

            var shader = Resources.Load<Shader>("Shaders/HeightFog");
            if (shader == null) shader = Shader.Find("KatKat/HeightFog");
            if (shader == null)
            {
                Debug.LogWarning("HeightFog shader bulunamadı; yükseklik sisi kapalı.");
                enabled = false;
                return;
            }

            _material = new Material(shader) { name = "KatKat_HeightFog" };
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "HeightFog";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = new Vector3(PlaneSize, PlaneSize, 1f);
            _plane = go.transform;
            _renderer = go.GetComponent<Renderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }

        private void LateUpdate()
        {
            if (_gm == null || _plane == null) return;
            var v = _gm.Visuals;
            var floor = _gm.ActiveFloor;
            bool show = v.heightFog && floor != null;
            _renderer.enabled = show;
            if (!show) return;

            float target = floor.BaseY - v.heightFogOffset;
            _y = _placed ? Mathf.Lerp(_y, target, 1f - Mathf.Exp(-FollowSpeed * Time.unscaledDeltaTime)) : target;
            _placed = true;

            Vector3 c = _cam.transform.position;
            _plane.position = new Vector3(c.x, _y, c.z);

            _material.SetColor(ColorId, v.heightFogColor);
            _material.SetFloat(TopYId, _y);
            _material.SetFloat(DensityId, v.heightFogDensity);
            _material.SetFloat(NoiseScaleId, v.heightFogNoiseScale);
            _material.SetFloat(NoiseStrengthId, v.heightFogNoise);
            _material.SetFloat(NoiseSpeedId, v.heightFogNoiseSpeed);
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
