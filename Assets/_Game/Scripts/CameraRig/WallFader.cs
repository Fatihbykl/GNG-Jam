using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Bebek evi kesiti: planlamada kameraya bakan dış duvarları gizler (gölgeleri kalır).
    /// Patlama başlayınca tüm duvarlar geri gelir.
    /// </summary>
    public class WallFader : MonoBehaviour
    {
        private const float FacingThreshold = 0.15f;

        private GameManager _gm;

        public void Init(GameManager gm) => _gm = gm;

        private void LateUpdate()
        {
            if (_gm == null || _gm.ActiveFloor == null) return;
            bool planning = _gm.State == GameState.Planning;
            Vector3 camPos = transform.position;

            foreach (var wall in _gm.ActiveFloor.Walls)
            {
                if (wall == null) continue;
                bool hide = false;
                if (planning)
                {
                    Vector3 toCam = camPos - wall.transform.position;
                    toCam.y = 0f;
                    hide = Vector3.Dot(wall.WorldOutward, toCam.normalized) > FacingThreshold;
                }

                wall.SetCutaway(hide);
            }
        }
    }
}
