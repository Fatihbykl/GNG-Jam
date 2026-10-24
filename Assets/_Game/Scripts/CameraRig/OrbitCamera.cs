using UnityEngine;
using UnityEngine.InputSystem;

namespace KatKat
{
    /// <summary>
    /// 360° yörünge kamerası. Sağ tık sürükle: döndür, tekerlek: yakınlaş, Q/E: 4 sabit köşe açısı.
    /// Patlamada otomatik geniş açıya geçer. Unscaled zamanla çalışır, ağır çekimden etkilenmez.
    /// </summary>
    public class OrbitCamera : MonoBehaviour
    {
        public float MinDistance = 6f;
        public float MaxDistance = 40f;
        public float MinPitch = 10f;
        public float MaxPitch = 85f;
        public float RotateSpeed = 0.25f;
        public float Smoothing = 10f;
        public float CinematicExtraDistance = 7f;
        public float CinematicMinPitch = 30f;

        /// <summary>Son sağ tık basışı sürüklemeye dönüştüyse true (tıklama olarak sayılmasın).</summary>
        public bool RightDragged { get; private set; }

        private float _yaw = 45f, _pitch = 50f, _distance = 18f;
        private float _yawTarget = 45f, _pitchTarget = 50f, _distanceTarget = 18f;
        private Vector3 _focus, _focusTarget;
        private bool _cinematic;
        private float _dragAccum;

        public void SetFocus(Vector3 point, bool snap = false)
        {
            _focusTarget = point;
            if (snap) _focus = point;
        }

        public void SetCinematic(bool on) => _cinematic = on;

        private void LateUpdate()
        {
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            if (mouse != null)
            {
                if (mouse.rightButton.wasPressedThisFrame) _dragAccum = 0f;
                if (mouse.rightButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    _dragAccum += delta.magnitude;
                    if (_dragAccum > 6f)
                    {
                        _yawTarget += delta.x * RotateSpeed;
                        _pitchTarget -= delta.y * RotateSpeed;
                    }
                }
                RightDragged = _dragAccum > 6f;

                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f && !GameHUD.IsPointerOverUI(mouse.position.ReadValue()))
                    _distanceTarget *= 1f - Mathf.Sign(scroll) * 0.1f;
            }

            if (kb != null)
            {
                if (kb.qKey.wasPressedThisFrame) _yawTarget = SnapCorner(_yawTarget - 90f);
                if (kb.eKey.wasPressedThisFrame) _yawTarget = SnapCorner(_yawTarget + 90f);
            }

            _pitchTarget = Mathf.Clamp(_pitchTarget, MinPitch, MaxPitch);
            _distanceTarget = Mathf.Clamp(_distanceTarget, MinDistance, MaxDistance);

            float pitchGoal = _cinematic ? Mathf.Max(_pitchTarget, CinematicMinPitch) : _pitchTarget;
            float distGoal = _cinematic ? _distanceTarget + CinematicExtraDistance : _distanceTarget;

            float k = 1f - Mathf.Exp(-Smoothing * Time.unscaledDeltaTime);
            _yaw = Mathf.LerpAngle(_yaw, _yawTarget, k);
            _pitch = Mathf.Lerp(_pitch, pitchGoal, k);
            _distance = Mathf.Lerp(_distance, distGoal, k * 0.6f);
            _focus = Vector3.Lerp(_focus, _focusTarget, k * 0.5f);

            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.position = _focus + rot * new Vector3(0f, 0f, -_distance);
            transform.rotation = rot;
        }

        private static float SnapCorner(float yaw) => Mathf.Round((yaw - 45f) / 90f) * 90f + 45f;
    }
}
