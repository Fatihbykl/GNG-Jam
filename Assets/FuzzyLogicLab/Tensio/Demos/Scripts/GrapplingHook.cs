using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Unity.Mathematics;

namespace Tensio.Demos
{
    [AddComponentMenu("Tensio/Demo/Grappling Hook")]
    public class GrapplingHook : MonoBehaviour
    {
        [Header("References")]
        public GameObject ropeThrowPosition;
        public FPSController playerController;
    
        [Header("Control Settings")] 
        public float maxDistance = 50f;
        public LayerMask grappleLayer = 1;

        [Header("Pull Mode Settings")] 
        public float pullSpeed = 25f;
        public float pullForcePower = 60f;
        public float maxPullVelocity = 35f;
    
        [Header("Release Settings")]
        public float releaseBoostMultiplier = 1.3f;

        [Header("Rope Physics")] 
        public float particleMass = 0.5f;

        [Header("Rope Visuals")] 
        public Material ropeMaterial;
        public float ropeWidth = 0.05f;

        // --- Private Variables ---
        private RopeController _ropePool;
        private RopeRenderer _rendererPool;
        private GameObject _ropeObj;

        private Transform _camTransform;
        private Rigidbody _playerRb;
        private bool _isHooked;
        private bool _isFlinging;
        private Vector3 _currentHitPoint;


        void Start()
        {
            Camera cam = GetComponentInChildren<Camera>();
            if (cam == null) cam = Camera.main;
            _camTransform = cam != null ? cam.transform : transform;
            _playerRb = gameObject.GetComponent<Rigidbody>();
        
            InitializeRopePool();
        }

        void InitializeRopePool()
        {
            _ropeObj = new GameObject("GrappleRope_Pooled");
            _ropePool = _ropeObj.AddComponent<RopeController>();
            _rendererPool = _ropeObj.AddComponent<RopeRenderer>();

            if (ropeMaterial != null) _rendererPool.Material = ropeMaterial;
            else
            {
                var mat = new Material(Shader.Find("Sprites/Default"));
                mat.color = Color.gray;
                _rendererPool.Material = mat;
            }

            _rendererPool.Radius = ropeWidth;
            _ropeObj.SetActive(false);
            DontDestroyOnLoad(_ropeObj);
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            bool shootPressed = Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
            bool shootReleased = Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;
#else
            bool shootPressed = Input.GetMouseButtonDown(0);
            bool shootReleased = Input.GetMouseButtonUp(0);
#endif

            if (shootPressed) ShootHook();
            else if (shootReleased) ReleaseHook();

            if (_isHooked && _ropePool != null && _ropePool.gameObject.activeSelf)
            {
                HandleDirectPull();
            }
            
            if (_isFlinging)
            {
                CheckIfLanded();
            }
        }

        void ShootHook()
        {
            if (_isHooked) return;

            Ray ray = new Ray(_camTransform.position, _camTransform.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, grappleLayer))
            {
                _currentHitPoint = hit.point;
                ActivateRope(hit);
            }
        }

        void ActivateRope(RaycastHit hit)
        {
            _ropeObj.transform.position = ropeThrowPosition.transform.position;
            _ropeObj.SetActive(true);

            if (playerController != null) playerController.SetGrapplingStatus(true);
        
#if UNITY_6000_0_OR_NEWER
            _playerRb.linearDamping = 0f;
#else
            _playerRb.drag = 0f;
#endif

            Vector3 startPos = ropeThrowPosition.transform.position;
            Vector3 endPos = hit.point;
            float dist = Vector3.Distance(startPos, endPos);

            _ropePool.InitSettings.Length = dist; 
            _ropePool.PhysicsSettings.Compliance = 0.0f; 
            _ropePool.AeroSettings.AirDrag = 0.5f; 

            int pCount = Mathf.Clamp((int)(dist * 1.5f), 4, 60);
            _ropePool.InitSettings.ParticleCount = pCount;
            _ropePool.InitSettings.PinFirstParticle = false;
            _ropePool.PhysicsSettings.ParticleMass = particleMass;
            _ropePool.PhysicsSettings.Gravity = Physics.gravity;

            _ropePool.PinConstraints.Clear();
            _ropePool.PinConstraints.Add(new RopeController.PinConstraint
            {
                ParticleIndex = 0,
                Target = transform,
                LocalOffset = Vector3.zero
            });
            _ropePool.PinConstraints.Add(new RopeController.PinConstraint
            {
                ParticleIndex = pCount - 1,
                Target = hit.transform,
                LocalOffset = hit.transform.InverseTransformPoint(hit.point)
            });

            _ropePool.InitializeRope();
            ForceLineShape(startPos, endPos, pCount);

            _isHooked = true;
            _isFlinging = false;
        }

        void HandleDirectPull()
        {
            _ropePool.PhysicsSettings.Compliance = 0.0f;

            float currentLen = _ropePool.CurrentLength;
            if (currentLen > 1.0f)
            {
                float newLen = currentLen - pullSpeed * Time.deltaTime;
                _ropePool.SetCurrentLength(Mathf.Max(0.5f, newLen));
            }

            Vector3 pullDir = (_currentHitPoint - transform.position).normalized;
            float distToTarget = Vector3.Distance(transform.position, _currentHitPoint);

            if (distToTarget > 1.5f)
            {
#if UNITY_6000_0_OR_NEWER
                if (_playerRb.linearVelocity.magnitude < maxPullVelocity)
#else
                if (_playerRb.velocity.magnitude < maxPullVelocity)
#endif
                {
                    _playerRb.AddForce(pullDir * pullForcePower, ForceMode.Acceleration);
                }
            }
        }

        void ForceLineShape(Vector3 start, Vector3 end, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                Vector3 straightPos = Vector3.Lerp(start, end, t);

                if (_ropePool.Positions.Length > i)
                {
                    float3 p = new float3(straightPos.x, straightPos.y, straightPos.z);
                    _ropePool.SetParticlePosition(i, p);

                    if (_ropePool.Velocities.Length > i)
                        _ropePool.SetVelocities(i, float3.zero);
                }
            }
        }

        void ReleaseHook()
        {
            if (_isHooked && _ropePool != null)
            {
                _ropePool.gameObject.SetActive(false);
            
#if UNITY_6000_0_OR_NEWER
                if (_playerRb.linearVelocity.magnitude > 5f)
#else
                if (_playerRb.velocity.magnitude > 5f)
#endif
                {
#if UNITY_6000_0_OR_NEWER
                    _playerRb.linearVelocity *= releaseBoostMultiplier;
#else
                    _playerRb.velocity *= releaseBoostMultiplier;
#endif
                }
            
                _isHooked = false;

                _isFlinging = true;
#if UNITY_6000_0_OR_NEWER
                _playerRb.linearDamping = 0.1f;
#else
                _playerRb.drag = 0.1f;
#endif
            }
        }

        void CheckIfLanded()
        {
            float groundCheckDistance = 1.1f; 
        
            if (Physics.SphereCast(transform.position, 0.3f, Vector3.down, out RaycastHit hit, groundCheckDistance))
            {
                if (playerController != null) playerController.SetGrapplingStatus(false);
            
                _isFlinging = false;
            }
        }
    }
}