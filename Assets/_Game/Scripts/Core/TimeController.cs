using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Ağır çekim için tek giriş noktası. timeScale ile fixedDeltaTime birlikte ölçeklenir,
    /// böylece Unity fiziği ve Tensio solver'ı yavaşlarken adım doğruluğu korunur.
    /// </summary>
    public static class TimeController
    {
        private static float _baseFixedDeltaTime = -1f;

        public static float CurrentScale { get; private set; } = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _baseFixedDeltaTime = -1f;
            CurrentScale = 1f;
        }

        public static float BaseFixedDeltaTime
        {
            get
            {
                if (_baseFixedDeltaTime < 0f) _baseFixedDeltaTime = Time.fixedDeltaTime;
                return _baseFixedDeltaTime;
            }
        }

        public static float FixedDeltaFor(float baseFixedDelta, float scale)
        {
            return baseFixedDelta * Mathf.Clamp(scale, 0.01f, 1f);
        }

        public static void SetScale(float scale)
        {
            scale = Mathf.Clamp(scale, 0.01f, 1f);
            float baseFixed = BaseFixedDeltaTime;
            CurrentScale = scale;
            Time.timeScale = scale;
            Time.fixedDeltaTime = FixedDeltaFor(baseFixed, scale);
        }

        public static void ResetTime() => SetScale(1f);
    }
}
