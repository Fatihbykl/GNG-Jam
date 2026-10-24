using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Gerçek bir çöküşte kat, desteğini erken kaybeden tarafa yatar ve eşyalar o yöne kayar. Bizde eşyaların
    /// üstünde durduğu zemin (alt katın tavanı) sabit olduğundan bu eğimi eşyalara çöküş yönünde yatay ivme olarak
    /// uygularız. Yön ve güç planlamadaki çöküş okundan gelir: ok ne kadar uzunsa eşyalar o kadar kayar.
    /// Her şarj patladığında eğim süresi yenilenir.
    /// </summary>
    public class CollapseShove : MonoBehaviour
    {
        private Floor _floor;
        private GameConfig _cfg;
        private Vector3 _direction;
        private float _accel;
        private float _until = -1f;

        public bool IsActive => _floor != null && Time.time < _until;

        /// <param name="direction">Yatay çöküş yönü; sıfırsa simetrik çöküş.</param>
        /// <param name="strength">0 = simetrik, 1 = tamamen tek yöne (bkz. <see cref="CollapsePreview.Strength"/>).</param>
        public void Arm(Floor floor, Vector3 direction, float strength, GameConfig cfg)
        {
            _floor = floor;
            _cfg = cfg;
            direction.y = 0f;
            _direction = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.zero;
            _accel = cfg.tiltAccel * Mathf.Lerp(cfg.tiltMinShare, 1f, Mathf.Clamp01(strength));
            _until = -1f;
        }

        public void OnChargeFired()
        {
            if (_floor != null) _until = Time.time + _cfg.tiltDuration;
        }

        public void Stop()
        {
            _floor = null;
            _until = -1f;
        }

        private void FixedUpdate()
        {
            if (!IsActive || _direction == Vector3.zero) return;

            foreach (var item in _floor.Items)
            {
                if (item == null) continue;
                var rb = item.Body;
                if (rb == null || rb.isKinematic || !rb.gameObject.activeInHierarchy) continue;
                float scale = item.Size == ItemSize.Big ? _cfg.tiltBigItemScale : 1f;
                rb.AddForce(_direction * (_accel * scale), ForceMode.Acceleration);
            }
        }
    }
}
