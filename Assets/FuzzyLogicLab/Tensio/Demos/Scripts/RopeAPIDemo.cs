using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Tensio.Demos
{
    /// <summary>
    /// An improved demonstration script showcasing the Force, Pinning, and Cutting APIs.
    /// Supports multiple ropes and dynamic global explosions.
    /// </summary>
    public class RopeAPIDemo : MonoBehaviour
    {
        [Header("Global Settings")] public float explosionPower = 500f;
        public float explosionRadius = 5f;
        public ParticleSystem particle;
        public Vector3 globalWind = new Vector3(20f, 0, 0);
        public Transform fixedExplosionAnchor;

        private enum InteractionMode
        {
            Drag,
            Cut,
            Explosion
        }

        private InteractionMode _currentMode = InteractionMode.Drag;

        // Interaction state
        private bool _isDragging = false;
        private int _draggedParticleIndex = -1;
        private RopeController _activeRope;
        private GameObject _mouseAnchor;
        private Rect _guiRect = new Rect(20, 20, 350, 300);

        private void Start()
        {
            _mouseAnchor = new GameObject("MouseAnchor_Demo");
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            Vector3 mousePos3D = Mouse.current != null ? (Vector3)Mouse.current.position.ReadValue() : Vector3.zero;
            bool getMouseButtonDown = Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
            bool getMouseButtonUp = Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;
#else
            Vector3 mousePos3D = Input.mousePosition;
            bool getMouseButtonDown = Input.GetMouseButtonDown(0);
            bool getMouseButtonUp = Input.GetMouseButtonUp(0);
#endif

            Vector2 mousePos = new Vector2(mousePos3D.x, Screen.height - mousePos3D.y);
            if (_guiRect.Contains(mousePos)) return;

            if (getMouseButtonDown)
            {
                HandleMouseDown(mousePos3D);
            }

            if (getMouseButtonUp)
            {
                HandleMouseUp();
            }

            if (_isDragging && _activeRope != null)
            {
                UpdateMouseAnchor(mousePos3D);
            }
        }

        private void HandleMouseDown(Vector3 mousePos)
        {
            Ray ray = Camera.main.ScreenPointToRay(mousePos);

            RopeController hitRope = null;
            float minHitDist = float.MaxValue;
            int hitSegmentIndex = -1;

            foreach (var rope in RopeController.AllRopes)
            {
                if (rope.Raycast(ray, out float hitDist, out int segmentIndex))
                {
                    if (hitDist < minHitDist)
                    {
                        minHitDist = hitDist;
                        hitRope = rope;
                        hitSegmentIndex = segmentIndex;
                    }
                }
            }

            if (hitRope != null)
            {
                Vector3 hitPoint = ray.GetPoint(minHitDist);

                switch (_currentMode)
                {
                    case InteractionMode.Drag:
                        _isDragging = true;
                        _activeRope = hitRope;
                        _draggedParticleIndex = hitSegmentIndex;
                        _mouseAnchor.transform.position = hitPoint;
                        // API: Pin specific particle to a transform
                        _activeRope.PinParticle(_draggedParticleIndex, _mouseAnchor.transform, hitPoint);
                        break;

                    case InteractionMode.Cut:
                        // API: Split rope at world position
                        hitRope.SplitAtWorldPosition(hitPoint);
                        break;

                    case InteractionMode.Explosion:
                        // API: Apply explosion to ALL ropes in radius
                        ApplyGlobalExplosion(hitPoint, explosionPower, explosionRadius);
                        break;
                }
            }
        }

        private void HandleMouseUp()
        {
            if (_isDragging && _activeRope != null)
            {
                // API: Release the pinned particle
                _activeRope.UnpinParticle(_draggedParticleIndex);
                _isDragging = false;
                _activeRope = null;
            }
        }

        private void ApplyGlobalExplosion(Vector3 pos, float power, float radius)
        {
            foreach (var rope in RopeController.AllRopes)
            {
                // API: Each rope handles its own internal particle distance checks
                rope.AddExplosionForce(power, pos, radius);
            }
        }

        private void UpdateMouseAnchor(Vector3 mousePos)
        {
            Plane plane = new Plane(-Camera.main.transform.forward, _mouseAnchor.transform.position);
            Ray ray = Camera.main.ScreenPointToRay(mousePos);
            if (plane.Raycast(ray, out float enter))
            {
                _mouseAnchor.transform.position = ray.GetPoint(enter);
            }
        }

        private void OnGUI()
        {
            _guiRect = GUILayout.Window(0, _guiRect, DrawWindow, "Tensio API Showcase");
        }

        private void DrawWindow(int windowID)
        {
            GUILayout.Label("<b>1. Global Forces</b>", new GUIStyle
            {
                richText = true, normal =
                {
                    textColor =
                        Color.gray
                }
            });

            if (GUILayout.Button("Global Wind (AddForceToAll)"))
            {
                foreach (var r in RopeController.AllRopes) r.AddForceToAll(globalWind);
            }

            if (fixedExplosionAnchor != null)
            {
                if (GUILayout.Button("Explosion at Anchor"))
                {
                    ApplyGlobalExplosion(fixedExplosionAnchor.position, explosionPower, explosionRadius);
                    particle.Play();
                }
            }

            GUILayout.Space(15);

            GUILayout.Label("<b>2. Interaction Mode</b>", new GUIStyle
            {
                richText = true, normal =
                {
                    textColor =
                        Color.gray
                }
            });
            // Using Toolbar to prevent the "stuck in cut mode" issue
            _currentMode = (InteractionMode)GUILayout.Toolbar((int)_currentMode,
                new string[] { "Drag", "Cut", "Explode" });

            GUILayout.Space(10);
            GUILayout.Label($"<i>Mode: {_currentMode}\n(Click on any rope in scene)</i>", new GUIStyle
            {
                richText
                    = true,
                fontSize = 11
            });

            GUILayout.Space(15);

            GUILayout.Label("<b>3. Constraints</b>", new GUIStyle
            {
                richText = true, normal =
                {
                    textColor =
                        Color.gray
                }
            });
            if (GUILayout.Button("Release All Pins (UnpinAll)"))
            {
                foreach (var r in RopeController.AllRopes) r.UnpinAll();
            }

            GUI.DragWindow();
        }
    }
}