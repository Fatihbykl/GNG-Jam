using UnityEngine;
using UnityEngine.InputSystem;

namespace KatKat
{
    /// <summary>
    /// Patlama sırasında 2–3 kez zamanı yavaşlatır: bir ip kopmak üzereyken, büyük bir eşya
    /// bölgeden çıkmak üzereyken ya da oyuncu Boşluk'a bastığında.
    /// </summary>
    public class SlowMoDirector : MonoBehaviour
    {
        private const float RampDownSpeed = 6f;
        private const float RampUpSpeed = 3f;
        private const float AutoCooldown = 1.5f;
        private const float PredictTime = 0.6f;
        private const float MinThreatSpeed = 2.5f;

        public int ChargesLeft { get; private set; }
        public bool IsActive { get; private set; }
        public bool IsMonitoring { get; private set; }
        public float RemainingRealtime { get; private set; }

        private GameManager _gm;
        private float _cooldown;
        private float _hitStop;

        public void Init(GameManager gm) => _gm = gm;

        public void Begin(int charges)
        {
            ChargesLeft = charges;
            IsActive = false;
            IsMonitoring = true;
            _cooldown = 0.5f;
        }

        public void End()
        {
            IsMonitoring = false;
            IsActive = false;
            _hitStop = 0f;
            TimeController.ResetTime();
        }

        /// <summary>
        /// Patlama anında zamanı çok kısa süre neredeyse durdurur, sonra normal hıza yumuşakça döner.
        /// Ağır çekim zaten açıksa dokunmaz.
        /// </summary>
        public void HitStop(float realSeconds, float scale)
        {
            if (!IsMonitoring || IsActive || realSeconds <= 0f) return;
            _hitStop = Mathf.Max(_hitStop, realSeconds);
            TimeController.SetScale(Mathf.Min(TimeController.CurrentScale, scale));
        }

        public bool TryTrigger()
        {
            if (!IsMonitoring || IsActive || ChargesLeft <= 0) return false;
            ChargesLeft--;
            IsActive = true;
            RemainingRealtime = _gm.Config.slowMoDuration;
            return true;
        }

        private void Update()
        {
            if (!IsMonitoring) return;
            float dt = Time.unscaledDeltaTime;

            if (IsActive)
            {
                RemainingRealtime -= dt;
                TimeController.SetScale(Mathf.MoveTowards(TimeController.CurrentScale, _gm.Config.slowMoScale, dt * RampDownSpeed));
                if (RemainingRealtime <= 0f)
                {
                    IsActive = false;
                    _cooldown = AutoCooldown;
                }
                return;
            }

            if (_hitStop > 0f) _hitStop -= dt;
            else if (TimeController.CurrentScale < 1f)
                TimeController.SetScale(Mathf.MoveTowards(TimeController.CurrentScale, 1f, dt * RampUpSpeed));

            var kb = Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame)
            {
                TryTrigger();
                return;
            }

            _cooldown -= dt;
            if (_cooldown <= 0f && ShouldAutoTrigger()) TryTrigger();
        }

        private bool ShouldAutoTrigger()
        {
            foreach (var link in _gm.Ropes)
            {
                if (link != null && link.Armed && link.ForceRatio > _gm.Config.slowMoAutoForceRatio) return true;
            }

            var floor = _gm.ActiveFloor;
            if (floor == null) return false;
            foreach (var item in floor.Items)
            {
                if (item == null || item.Size != ItemSize.Big || item.Body.isKinematic) continue;
                Vector3 v = item.Body.linearVelocity;
                if (v.magnitude < MinThreatSpeed) continue;
                Vector3 p = item.Body.worldCenterOfMass;
                if (_gm.Zone.Contains(p) && !_gm.Zone.Contains(p + v * PredictTime)) return true;
            }

            return false;
        }
    }
}
