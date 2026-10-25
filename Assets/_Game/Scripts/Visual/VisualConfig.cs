using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace KatKat
{
    /// <summary>
    /// Işık, gökyüzü, sis ve post-processing ayarları tek yerde. Editörde Play sırasında canlı uygulanır
    /// (bkz. <see cref="LookDirector"/>); beğenilen değerler Play'den çıkmadan önce kopyalanmalı.
    /// </summary>
    [Serializable]
    public class VisualConfig
    {
        [Header("Güneş")]
        public Color sunColor = new Color(1f, 0.93f, 0.82f);
        public float sunIntensity = 2.3f;
        [Range(10f, 90f)] public float sunPitch = 44f;
        public float sunYaw = -35f;
        [Range(0f, 1f)] public float shadowStrength = 0.7f;

        [Header("Dolgu ışığı (kamerayla döner, gölgesiz)")]
        public Color fillColor = new Color(0.6f, 0.72f, 1f);
        public float fillIntensity = 0.5f;
        [Range(0f, 60f)] public float fillPitch = 20f;

        [Header("Ortam ışığı (gradient)")]
        public Color ambientSky = new Color(0.7f, 0.8f, 0.98f);
        public Color ambientEquator = new Color(0.86f, 0.78f, 0.7f);
        public Color ambientGround = new Color(0.4f, 0.37f, 0.38f);

        [Header("Gökyüzü (procedural)")]
        public Color skyTint = new Color(0.4f, 0.6f, 0.95f);
        public Color skyGround = new Color(0.36f, 0.38f, 0.44f);
        [Range(0f, 5f)] public float atmosphereThickness = 0.9f;
        [Range(0f, 8f)] public float skyExposure = 1.4f;
        [Tooltip("Gökyüzü yoksa (materyal bulunamazsa) kullanılan düz renk.")]
        public Color fallbackBackground = new Color(0.62f, 0.8f, 0.95f);

        [Header("Sis (aşağıdaki katlar kaybolur)")]
        public bool fog = true;
        public Color fogColor = new Color(0.74f, 0.82f, 0.94f);
        public float fogStart = 35f;
        public float fogEnd = 130f;

        [Header("Post-processing")]
        public TonemappingMode tonemapping = TonemappingMode.ACES;
        public float postExposure = 0.7f;
        [Range(-100f, 100f)] public float contrast = 6f;
        [Range(-100f, 100f)] public float saturation = 22f;
        public float bloomIntensity = 0.45f;
        public float bloomThreshold = 1.1f;
        [Range(0f, 1f)] public float bloomScatter = 0.6f;
        [Range(0f, 1f)] public float vignette = 0.24f;
        [Tooltip("Beyaz dengesi: + sıcak (sarı), − soğuk (mavi).")]
        [Range(-100f, 100f)] public float temperature = 8f;
        [Range(-100f, 100f)] public float tint = 2f;

        [Header("Patlama hissi")]
        [Tooltip("Açık: kodla kurulan patlama efekti. Kapalı: GameManager.ExplosionVfxPrefab kullanılır.")]
        public bool useCustomBlastFx = true;
        [Tooltip("Her patlamada zamanın kısa süre neredeyse durması (gerçek saniye). 0 = kapalı.")]
        public float hitStopDuration = 0.06f;
        [Range(0.01f, 1f)] public float hitStopScale = 0.05f;

        [Header("Ağır çekim efekti")]
        [Tooltip("Ağır çekimde görüş açısının daralması (derece).")]
        public float slowMoFovZoom = 6f;
        [Range(-100f, 0f)] public float slowMoSaturation = -40f;
        [Range(0f, 1f)] public float slowMoChromatic = 0.45f;
        [Range(0f, 1f)] public float slowMoVignette = 0.42f;
        public Color slowMoTint = new Color(0.86f, 0.93f, 1f);
        [Tooltip("Efektin girip çıkma hızı (gerçek zaman).")]
        public float slowMoBlendSpeed = 6f;
    }
}
