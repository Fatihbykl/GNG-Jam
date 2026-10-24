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
        public Color sunColor = new Color(1f, 0.91f, 0.78f);
        public float sunIntensity = 1.7f;
        [Range(10f, 90f)] public float sunPitch = 38f;
        public float sunYaw = -35f;
        [Range(0f, 1f)] public float shadowStrength = 0.8f;

        [Header("Dolgu ışığı (kamerayla döner, gölgesiz)")]
        public Color fillColor = new Color(0.62f, 0.72f, 1f);
        public float fillIntensity = 0.35f;
        [Range(0f, 60f)] public float fillPitch = 20f;

        [Header("Ortam ışığı (gradient)")]
        public Color ambientSky = new Color(0.58f, 0.68f, 0.86f);
        public Color ambientEquator = new Color(0.72f, 0.66f, 0.6f);
        public Color ambientGround = new Color(0.26f, 0.25f, 0.28f);

        [Header("Gökyüzü (procedural)")]
        public Color skyTint = new Color(0.45f, 0.62f, 0.9f);
        public Color skyGround = new Color(0.36f, 0.38f, 0.44f);
        [Range(0f, 5f)] public float atmosphereThickness = 0.8f;
        [Range(0f, 8f)] public float skyExposure = 1.25f;
        [Tooltip("Gökyüzü yoksa (materyal bulunamazsa) kullanılan düz renk.")]
        public Color fallbackBackground = new Color(0.62f, 0.8f, 0.95f);

        [Header("Sis (aşağıdaki katlar kaybolur)")]
        public bool fog = true;
        public Color fogColor = new Color(0.66f, 0.74f, 0.86f);
        public float fogStart = 30f;
        public float fogEnd = 120f;

        [Header("Post-processing")]
        public TonemappingMode tonemapping = TonemappingMode.Neutral;
        public float postExposure = 0.15f;
        [Range(-100f, 100f)] public float contrast = 12f;
        [Range(-100f, 100f)] public float saturation = 10f;
        public float bloomIntensity = 0.6f;
        public float bloomThreshold = 0.95f;
        [Range(0f, 1f)] public float bloomScatter = 0.65f;
        [Range(0f, 1f)] public float vignette = 0.22f;

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
