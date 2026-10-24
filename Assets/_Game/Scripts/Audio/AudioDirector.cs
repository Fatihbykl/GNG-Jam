using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Ses çalar: olay sesleri için bir AudioSource havuzu, müzik/ortam/ip gıcırtısı döngüleri ve ağır çekim filtresi.
    /// Dünyadaki sesler ağır çekimde pesleşir, tüm sesler alçak geçiren filtreyle boğuklaşır, müzik kısılır.
    /// Bankada klibi olmayan olaylar sessizce atlanır.
    /// </summary>
    public class AudioDirector : MonoBehaviour
    {
        private const int PoolSize = 24;
        private const float OpenCutoff = 22000f;
        private const float MinPitch = 0.4f;
        // Ağır çekimin dünya seslerinin perdesine etkisi (0 = yok, 1 = zaman ölçeği kadar)
        private const float SlowMoPitchInfluence = 0.5f;
        private const float CreakThreshold = 0.5f;

        public static AudioDirector Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private GameManager _gm;
        private SoundBankSO _bank;
        private AudioSource[] _pool;
        private int _next;
        private float[] _lastPlayed;
        private AudioSource _music, _ambience, _creak;
        private AudioLowPassFilter _lowPass;

        public void Init(GameManager gm, SoundBankSO bank, Camera cam)
        {
            Instance = this;
            _gm = gm;
            _bank = bank;
            _lastPlayed = new float[System.Enum.GetValues(typeof(Sound)).Length];
            for (int i = 0; i < _lastPlayed.Length; i++) _lastPlayed[i] = -100f;

            _pool = new AudioSource[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                var src = new GameObject($"Sfx_{i}").AddComponent<AudioSource>();
                src.transform.SetParent(transform, false);
                src.playOnAwake = false;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 10f;
                src.maxDistance = 90f;
                _pool[i] = src;
            }

            _music = CreateLoop("Music");
            _ambience = CreateLoop("Ambience");
            _creak = CreateLoop("RopeCreak");

            var listener = cam.GetComponent<AudioListener>();
            if (listener == null) listener = cam.gameObject.AddComponent<AudioListener>();
            _lowPass = cam.GetComponent<AudioLowPassFilter>();
            if (_lowPass == null) _lowPass = cam.gameObject.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = OpenCutoff;

            if (_bank == null) return;
            StartLoop(_music, _bank.Music, _bank.MusicVolume);
            StartLoop(_ambience, _bank.Ambience, _bank.AmbienceVolume);
        }

        /// <summary>Olay sesini çalar. <paramref name="position"/> verilirse sahnede o noktadan gelir.</summary>
        public static void Play(Sound sound, Vector3? position = null, float volumeScale = 1f)
        {
            if (Instance != null) Instance.PlayInternal(sound, position, volumeScale);
        }

        /// <summary>Çarpışma sesi: hızla ölçeklenir, yavaş temaslarda çalmaz.</summary>
        public static void Impact(Sound sound, Collision collision, float minSpeed = 2.5f, float fullSpeed = 10f)
        {
            if (Instance == null) return;
            float speed = collision.relativeVelocity.magnitude;
            if (speed < minSpeed) return;
            Vector3 p = collision.contactCount > 0 ? collision.GetContact(0).point : collision.transform.position;
            Instance.PlayInternal(sound, p, Mathf.InverseLerp(minSpeed, fullSpeed, speed) * 0.7f + 0.3f);
        }

        private void PlayInternal(Sound sound, Vector3? position, float volumeScale)
        {
            if (_bank == null) return;
            var ev = _bank.Get(sound);
            if (ev == null || !ev.HasClip) return;

            int id = (int)sound;
            float now = Time.unscaledTime;
            if (now - _lastPlayed[id] < ev.Cooldown) return;
            _lastPlayed[id] = now;

            var src = _pool[_next];
            _next = (_next + 1) % _pool.Length;

            float pitch = 1f + Random.Range(-ev.PitchJitter, ev.PitchJitter);
            if (position.HasValue)
            {
                src.transform.position = position.Value;
                src.spatialBlend = 0.6f;
                pitch *= Mathf.Lerp(1f, TimeController.CurrentScale, SlowMoPitchInfluence);
            }
            else
            {
                src.transform.localPosition = Vector3.zero;
                src.spatialBlend = 0f;
            }

            src.clip = ev.Pick();
            src.pitch = Mathf.Max(MinPitch, pitch);
            src.volume = ev.Volume * _bank.MasterVolume * Mathf.Clamp01(volumeScale);
            src.Play();
        }

        private void Update()
        {
            if (_bank == null || _gm == null) return;
            float w = _gm.Look != null ? _gm.Look.SlowMoWeight : 0f;

            _lowPass.cutoffFrequency = Mathf.Lerp(OpenCutoff, _bank.SlowMoLowPass, w);
            _music.volume = _bank.MusicVolume * _bank.MasterVolume * Mathf.Lerp(1f, 0.5f, w);
            _music.pitch = Mathf.Lerp(1f, 0.85f, w);
            _ambience.volume = _bank.AmbienceVolume * _bank.MasterVolume;

            UpdateCreak();
        }

        /// <summary>En gergin ipin gerilimine göre gıcırtı döngüsünün sesi ve perdesi.</summary>
        private void UpdateCreak()
        {
            if (_bank.RopeCreakLoop == null) return;

            float ratio = 0f;
            foreach (var link in _gm.Ropes)
            {
                if (link != null && link.Armed && link.IsHolding) ratio = Mathf.Max(ratio, link.ForceRatio);
            }

            float k = Mathf.InverseLerp(CreakThreshold, 1f, ratio);
            if (k <= 0f)
            {
                if (_creak.isPlaying) _creak.Stop();
                return;
            }

            if (_creak.clip != _bank.RopeCreakLoop) _creak.clip = _bank.RopeCreakLoop;
            if (!_creak.isPlaying) _creak.Play();
            _creak.volume = _bank.RopeCreakVolume * _bank.MasterVolume * k;
            _creak.pitch = 0.8f + 0.4f * k;
        }

        private AudioSource CreateLoop(string name)
        {
            var src = new GameObject(name).AddComponent<AudioSource>();
            src.transform.SetParent(transform, false);
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            return src;
        }

        private static void StartLoop(AudioSource src, AudioClip clip, float volume)
        {
            if (clip == null) return;
            src.clip = clip;
            src.volume = volume;
            src.Play();
        }
    }
}
