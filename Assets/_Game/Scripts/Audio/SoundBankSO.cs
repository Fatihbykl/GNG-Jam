using System;
using UnityEngine;

namespace KatKat
{
    public enum Sound
    {
        Explosion,
        ConcreteBreak,
        DebrisImpact,
        ItemImpact,
        Detonate,
        RopePlace,
        RopeRemove,
        RopeSnap,
        RopeReinforce,
        SlowMoEnter,
        SlowMoExit,
        ZoneWarning,
        UiClick,
        ResultsTick,
        LifeLost,
        FloorStart,
        GameOver
    }

    /// <summary>Tek bir oyun olayının sesi. Birden çok klip verilirse her çalışta rastgele biri seçilir.</summary>
    [Serializable]
    public class SoundEvent
    {
        public AudioClip[] Clips = Array.Empty<AudioClip>();
        [Range(0f, 1f)] public float Volume = 1f;
        [Tooltip("Her çalışta perdeye eklenen rastgele sapma (±).")]
        [Range(0f, 0.5f)] public float PitchJitter = 0.06f;
        [Tooltip("Aynı ses en erken bu kadar sonra tekrar çalar (gerçek saniye). Çok sayıda çarpışmada sesi boğmaz.")]
        public float Cooldown = 0.03f;

        public SoundEvent() { }

        public SoundEvent(float volume, float pitchJitter, float cooldown)
        {
            Volume = volume;
            PitchJitter = pitchJitter;
            Cooldown = cooldown;
        }

        public bool HasClip => Clips != null && Clips.Length > 0;

        public AudioClip Pick()
        {
            if (!HasClip) return null;
            return Clips[UnityEngine.Random.Range(0, Clips.Length)];
        }
    }

    /// <summary>
    /// Oyunun tüm sesleri tek yerde. Boş bırakılan olaylar sessizce atlanır; klipleri istediğin zaman ekleyebilirsin.
    /// </summary>
    [CreateAssetMenu(menuName = "Kat Kat Yıkım/Sound Bank", fileName = "SoundBank")]
    public class SoundBankSO : ScriptableObject
    {
        [Range(0f, 1f)] public float MasterVolume = 0.9f;

        [Header("Patlama ve yıkım")]
        public SoundEvent Explosion = new SoundEvent(1f, 0.1f, 0.05f);
        [Tooltip("Cell Fracture parçası kırılınca.")]
        public SoundEvent ConcreteBreak = new SoundEvent(0.8f, 0.12f, 0.08f);
        [Tooltip("Yapı parçası ya da molozun sert çarpması.")]
        public SoundEvent DebrisImpact = new SoundEvent(0.55f, 0.15f, 0.06f);
        [Tooltip("Eşyanın sert çarpması.")]
        public SoundEvent ItemImpact = new SoundEvent(0.6f, 0.12f, 0.08f);
        [Tooltip("PATLAT'a basıldığında (ör. fitil, alarm).")]
        public SoundEvent Detonate = new SoundEvent(0.8f, 0f, 0.2f);

        [Header("İpler")]
        public SoundEvent RopePlace = new SoundEvent(0.7f, 0.08f, 0.05f);
        public SoundEvent RopeRemove = new SoundEvent(0.6f, 0.08f, 0.05f);
        public SoundEvent RopeSnap = new SoundEvent(0.9f, 0.1f, 0.05f);
        public SoundEvent RopeReinforce = new SoundEvent(0.7f, 0.05f, 0.1f);
        [Tooltip("Gerilen iplerin gıcırtısı (döngü). Ses en gergin ipe göre yükselir.")]
        public AudioClip RopeCreakLoop;
        [Range(0f, 1f)] public float RopeCreakVolume = 0.5f;

        [Header("Ağır çekim")]
        public SoundEvent SlowMoEnter = new SoundEvent(0.8f, 0f, 0.2f);
        public SoundEvent SlowMoExit = new SoundEvent(0.6f, 0f, 0.2f);
        [Tooltip("Ağır çekimde tüm seslere uygulanan alçak geçiren filtre (Hz).")]
        public float SlowMoLowPass = 900f;

        [Header("Geri bildirim ve arayüz")]
        [Tooltip("Eşya güvenli bölgeden çıktığında.")]
        public SoundEvent ZoneWarning = new SoundEvent(0.7f, 0.04f, 0.3f);
        public SoundEvent UiClick = new SoundEvent(0.5f, 0.05f, 0.03f);
        [Tooltip("Sonuç ekranında her satır gelirken.")]
        public SoundEvent ResultsTick = new SoundEvent(0.5f, 0.03f, 0.05f);
        public SoundEvent LifeLost = new SoundEvent(0.9f, 0f, 0.3f);
        [Tooltip("Yeni kat başlarken (başlık gelirken).")]
        public SoundEvent FloorStart = new SoundEvent(0.7f, 0f, 0.5f);
        public SoundEvent GameOver = new SoundEvent(0.9f, 0f, 1f);

        [Header("Müzik ve ortam (döngü)")]
        public AudioClip Music;
        [Range(0f, 1f)] public float MusicVolume = 0.35f;
        [Tooltip("Rüzgâr, şehir uğultusu vb.")]
        public AudioClip Ambience;
        [Range(0f, 1f)] public float AmbienceVolume = 0.3f;

        public SoundEvent Get(Sound sound)
        {
            switch (sound)
            {
                case Sound.Explosion: return Explosion;
                case Sound.ConcreteBreak: return ConcreteBreak;
                case Sound.DebrisImpact: return DebrisImpact;
                case Sound.ItemImpact: return ItemImpact;
                case Sound.Detonate: return Detonate;
                case Sound.RopePlace: return RopePlace;
                case Sound.RopeRemove: return RopeRemove;
                case Sound.RopeSnap: return RopeSnap;
                case Sound.RopeReinforce: return RopeReinforce;
                case Sound.SlowMoEnter: return SlowMoEnter;
                case Sound.SlowMoExit: return SlowMoExit;
                case Sound.ZoneWarning: return ZoneWarning;
                case Sound.UiClick: return UiClick;
                case Sound.ResultsTick: return ResultsTick;
                case Sound.LifeLost: return LifeLost;
                case Sound.FloorStart: return FloorStart;
                case Sound.GameOver: return GameOver;
                default: return null;
            }
        }
    }
}
