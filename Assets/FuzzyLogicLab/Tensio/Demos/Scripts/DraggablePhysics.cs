using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Tensio.Demos
{
    [AddComponentMenu("Tensio/Demo/Draggable Physics")]
    [RequireComponent(typeof(LineRenderer))]
    public class DraggablePhysics : MonoBehaviour
    {
        [Header("Drag Settings")]
        public float springForce = 500f;
        public float damper = 5f;
        public float dragDistance = 0.2f;
    
        [Header("Visuals")]
        public Color dragColor = Color.cyan;
        public float lineWidth = 0.05f;

        private SpringJoint _currentJoint;
        private Rigidbody _targetRigidbody;
        private LineRenderer _lineRenderer;
        private float _distanceToCamera;

        void Start()
        {
            _lineRenderer = GetComponent<LineRenderer>();
            _lineRenderer.startWidth = lineWidth;
            _lineRenderer.endWidth = lineWidth;
            _lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _lineRenderer.startColor = dragColor;
            _lineRenderer.endColor = new Color(dragColor.r, dragColor.g, dragColor.b, 0f);
            _lineRenderer.enabled = false;
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            bool getMouseButtonDown = Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
            bool getMouseButtonUp = Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;
            Vector3 mousePos = Mouse.current != null ? (Vector3)Mouse.current.position.ReadValue() : Vector3.zero;
#else
            bool getMouseButtonDown = Input.GetMouseButtonDown(0);
            bool getMouseButtonUp = Input.GetMouseButtonUp(0);
            Vector3 mousePos = Input.mousePosition;
#endif

            if (getMouseButtonDown)
            {
                RaycastHit hit;
                Ray ray = Camera.main.ScreenPointToRay(mousePos);

                if (Physics.Raycast(ray, out hit))
                {
                    if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                    {
                        StartDragging(hit.rigidbody, hit.point);
                    }
                }
            }

            if (getMouseButtonUp)
            {
                StopDragging();
            }

            if (_currentJoint != null)
            {
                mousePos.z = _distanceToCamera;
                Vector3 worldTarget = Camera.main.ScreenToWorldPoint(mousePos);

                _currentJoint.connectedAnchor = worldTarget;

                _lineRenderer.SetPosition(0, _targetRigidbody.transform.position);
                _lineRenderer.SetPosition(1, worldTarget);
            }
        }

        void StartDragging(Rigidbody target, Vector3 hitPoint)
        {
            _targetRigidbody = target;
        
            _distanceToCamera = Vector3.Distance(Camera.main.transform.position, target.transform.position);

            _currentJoint = target.gameObject.AddComponent<SpringJoint>();
        
            _currentJoint.spring = springForce;
            _currentJoint.damper = damper;
            _currentJoint.maxDistance = dragDistance;
            _currentJoint.autoConfigureConnectedAnchor = false;
            
            _currentJoint.anchor = Vector3.zero; 
        
            _currentJoint.connectedAnchor = hitPoint;

            _lineRenderer.enabled = true;
        }

        void StopDragging()
        {
            if (_currentJoint != null)
            {
                Destroy(_currentJoint);
                _currentJoint = null;
            }
        
            _targetRigidbody = null;
            _lineRenderer.enabled = false;
        }
    }
}