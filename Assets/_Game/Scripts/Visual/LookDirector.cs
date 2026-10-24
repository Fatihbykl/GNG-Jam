using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace KatKat
{
    /// <summary>
    /// Sahnenin görünümü: güneş, kamerayla dönen dolgu ışığı, gradient ortam ışığı, procedural gökyüzü, sis ve
    /// çalışma anında kurulan global post-processing Volume'u. Ağır çekimde renkler soğur, kenarlarda renk kayması
    /// ve vinyet belirir. Değerler <see cref="VisualConfig"/>'ten her kare okunur; Inspector'dan canlı ayarlanabilir.
    /// </summary>
    public class LookDirector : MonoBehaviour
    {
        private GameManager _gm;
        private Camera _cam;
        private Light _sun;
        private Light _fill;
        private Material _sky;
        private VolumeProfile _profile;
        private Tonemapping _tonemapping;
        private ColorAdjustments _color;
        private Bloom _bloom;
        private Vignette _vignette;
        private ChromaticAberration _chromatic;
        private float _slowMoWeight;

        /// <summary>Ağır çekim efektinin anlık gücü (0–1); ses filtresi de bunu izler.</summary>
        public float SlowMoWeight => _slowMoWeight;
        private float _baseFov;

        public void Init(GameManager gm, Camera cam)
        {
            _gm = gm;
            _cam = cam;
            _baseFov = cam.fieldOfView;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            BuildVolume();

            _sun = FindSun();
            _fill = new GameObject("FillLight").AddComponent<Light>();
            _fill.transform.SetParent(transform, false);
            _fill.type = LightType.Directional;
            _fill.shadows = LightShadows.None;

            // Varsayılan skybox procedural; kopyasını renklendiririz ki asset değişmesin.
            if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_SkyTint"))
            {
                _sky = new Material(RenderSettings.skybox) { name = "KatKat_Sky" };
                RenderSettings.skybox = _sky;
            }
            cam.clearFlags = _sky != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;

            _slowMoWeight = 0f;
            Apply();
        }

        private void BuildVolume()
        {
            var go = new GameObject("PostVolume");
            go.transform.SetParent(transform, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "KatKat_Post";
            _tonemapping = _profile.Add<Tonemapping>(true);
            _color = _profile.Add<ColorAdjustments>(true);
            _bloom = _profile.Add<Bloom>(true);
            _vignette = _profile.Add<Vignette>(true);
            _chromatic = _profile.Add<ChromaticAberration>(true);
            volume.sharedProfile = _profile;
        }

        private static Light FindSun()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional) return light;
            }

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            return sun;
        }

        private void Update()
        {
            if (_gm == null) return;

            // Ağır çekim ne kadar derinse efekt o kadar güçlü
            float slowScale = Mathf.Min(_gm.Config.slowMoScale, 0.99f);
            // Hit-stop'ta efekt yanıp sönmesin: yalnızca ağır çekim penceresi açıkken
            float target = _gm.SlowMo.IsActive ? 1f - Mathf.InverseLerp(slowScale, 1f, TimeController.CurrentScale) : 0f;
            _slowMoWeight = Mathf.MoveTowards(_slowMoWeight, target, _gm.Visuals.slowMoBlendSpeed * Time.unscaledDeltaTime);
            Apply();
        }

        private void Apply()
        {
            var v = _gm.Visuals;
            float w = _slowMoWeight;

            _sun.color = v.sunColor;
            _sun.intensity = v.sunIntensity;
            _sun.shadows = LightShadows.Soft;
            _sun.shadowStrength = v.shadowStrength;
            _sun.transform.rotation = Quaternion.Euler(v.sunPitch, v.sunYaw, 0f);
            RenderSettings.sun = _sun;

            _fill.color = v.fillColor;
            _fill.intensity = v.fillIntensity;
            _fill.transform.rotation = Quaternion.Euler(v.fillPitch, _cam.transform.eulerAngles.y, 0f);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = v.ambientSky;
            RenderSettings.ambientEquatorColor = v.ambientEquator;
            RenderSettings.ambientGroundColor = v.ambientGround;

            if (_sky != null)
            {
                _sky.SetColor("_SkyTint", v.skyTint);
                _sky.SetColor("_GroundColor", v.skyGround);
                _sky.SetFloat("_AtmosphereThickness", v.atmosphereThickness);
                _sky.SetFloat("_Exposure", v.skyExposure);
            }
            _cam.backgroundColor = v.fallbackBackground;

            RenderSettings.fog = v.fog;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = v.fogColor;
            RenderSettings.fogStartDistance = v.fogStart;
            RenderSettings.fogEndDistance = v.fogEnd;

            _tonemapping.mode.value = v.tonemapping;
            _color.postExposure.value = v.postExposure;
            _color.contrast.value = v.contrast;
            _color.saturation.value = Mathf.Lerp(v.saturation, v.slowMoSaturation, w);
            _color.colorFilter.value = Color.Lerp(Color.white, v.slowMoTint, w);
            _bloom.intensity.value = v.bloomIntensity;
            _bloom.threshold.value = v.bloomThreshold;
            _bloom.scatter.value = v.bloomScatter;
            _vignette.intensity.value = Mathf.Lerp(v.vignette, v.slowMoVignette, w);
            _vignette.smoothness.value = 0.45f;
            _chromatic.intensity.value = v.slowMoChromatic * w;
            _cam.fieldOfView = _baseFov - v.slowMoFovZoom * w;
        }

        private void OnDestroy()
        {
            if (_profile != null) Destroy(_profile);
            if (_sky != null) Destroy(_sky);
        }
    }
}
