using UnityEngine;

namespace Tensio
{
    [AddComponentMenu("Tensio/Rendering/Rope LOD Controller")]
    [RequireComponent(typeof(RopeController))]
    public class RopeLODController : MonoBehaviour
    {
        [Header("References")]
        public RopeRenderer HighQualityRenderer;
        public LineRenderer LowQualityRenderer;

        [Header("LOD Thresholds")]
        [Tooltip("Mesh -> Line transition distance")]
        public float DistanceMeshToLine = 10f; 

        [Tooltip("Line -> Cull transition distance")]
        public float DistanceLineToCull = 40f;

        [Tooltip("Suspend physics distance")]
        public float DistanceSuspendPhysics = 100f;

        [Header("Bounding Sphere Settings")]
        [Range(0.1f, 1.5f)]
        public float RadiusMultiplier = 0.5f; 
        public bool OffsetCenterDown = true;

        [Header("Optimization")]
        public float CheckInterval = 0.1f;

        [Header("Physics Optimization")]
        [Tooltip("Physics quality (substeps) when in High Quality (Mesh) mode.")]
        public int LOD0_Substeps = 8;
        [Tooltip("Physics quality (substeps) when in Medium Quality (Line) mode.")]
        public int LOD1_Substeps = 4;
        [Tooltip("Physics quality (substeps) when in Low Quality (Invisible) mode.")]
        public int LOD2_Substeps = 1;

        [Header("Debug Info (Read Only)")]
        [SerializeField] private int _currentLOD = 0;
        [SerializeField] private bool _isVisible = true;
        [SerializeField] private float _calculatedDistance = 0f;
        [SerializeField] private string _status = "Initializing";

        public int CurrentLOD => _currentLOD;
        public float CurrentDistance => _calculatedDistance;
        public string DebugStatus => _status;

        private RopeController _ropeController;
        private CullingGroup _cullingGroup;
        private BoundingSphere[] _boundingSpheres;
        private Vector3[] _linePositionsBuffer;
        private Camera _targetCamera;
        private float _nextCheckTime;

        private void Awake()
        {
            _ropeController = GetComponent<RopeController>();
            if (HighQualityRenderer == null) HighQualityRenderer = GetComponent<RopeRenderer>();
            
            // Capture initial settings as default for LOD0 if needed, or override if set in inspector
            // Here we assume the inspector values for LOD are the source of truth for "Target Quality"
            if (_ropeController != null)
            {
                // Optional: Sync LOD0 with initial controller setting if user prefers that workflow
                // LOD0_Substeps = _ropeController.Settings.SubSteps; 
            }

            InitializeLineRenderer();
        }

        private void Start()
        {
            CreateCullingGroup();
        }

        private void OnEnable()
        {
            if (_cullingGroup == null) CreateCullingGroup();
            else _cullingGroup.enabled = true;
        }

        private void OnDisable()
        {
            if (_cullingGroup != null) _cullingGroup.enabled = false;
            if (LowQualityRenderer != null) LowQualityRenderer.enabled = false;
        }

        private void OnDestroy()
        {
            CleanupCullingGroup();
        }

        private void CreateCullingGroup()
        {
            if (_cullingGroup != null) CleanupCullingGroup();

            _cullingGroup = new CullingGroup();
            _targetCamera = Camera.main;
            _cullingGroup.targetCamera = _targetCamera;
            
            _boundingSpheres = new BoundingSphere[1];
            UpdateBoundingSphereData();
            _cullingGroup.SetBoundingSpheres(_boundingSpheres);
            _cullingGroup.SetBoundingSphereCount(1);

            _cullingGroup.onStateChanged += OnVisibilityChanged;
            _cullingGroup.enabled = true;
        }

        private void CleanupCullingGroup()
        {
            if (_cullingGroup != null)
            {
                _cullingGroup.onStateChanged -= OnVisibilityChanged;
                _cullingGroup.Dispose();
                _cullingGroup = null;
            }
        }

        private void OnVisibilityChanged(CullingGroupEvent evt)
        {
            if (evt.hasBecomeVisible) _isVisible = true;
            if (evt.hasBecomeInvisible) _isVisible = false;
            
            EvaluateLOD();
        }

        private void Update()
        {
            if (_targetCamera == null) _targetCamera = Camera.main;
            if (_targetCamera == null) return;

            UpdateBoundingSphereData();
            
            if (Time.time >= _nextCheckTime)
            {
                CalculateDistanceAndLOD();
                _nextCheckTime = Time.time + CheckInterval;
            }
        }

        private void UpdateBoundingSphereData()
        {
            if (_ropeController == null || _boundingSpheres == null) return;

            float len = _ropeController.CurrentLength;
            float radius = len * RadiusMultiplier;
            Vector3 center = transform.position;
            
            if (OffsetCenterDown) center += Vector3.down * (len * 0.5f);

            _boundingSpheres[0].position = center;
            _boundingSpheres[0].radius = radius;
        }

        private void CalculateDistanceAndLOD()
        {
            if (_targetCamera == null || _boundingSpheres == null) return;

            float distToCenter = Vector3.Distance(_boundingSpheres[0].position, _targetCamera.transform.position);
            
            _calculatedDistance = Mathf.Max(0f, distToCenter - _boundingSpheres[0].radius);

            EvaluateLOD();
        }

        private void EvaluateLOD()
        {
            if (_calculatedDistance > DistanceSuspendPhysics)
            {
                SetLODState(3);
                _status = "LOD 3 (Suspend)";
                return;
            }

            if (!_isVisible)
            {
                SetLODState(2); // Cull Visuals
                _status = "Invisible (Physics On)";
                return;
            }

            if (_calculatedDistance < DistanceMeshToLine)
            {
                SetLODState(0); // Mesh
                _status = "LOD 0 (Mesh)";
            }
            else if (_calculatedDistance < DistanceLineToCull)
            {
                SetLODState(1); // Line
                _status = "LOD 1 (Line)";
            }
            else
            {
                SetLODState(2); // Cull Visuals
                _status = "LOD 2 (Too Far)";
            }
        }

        private void SetLODState(int lodLevel)
        {
            if (_currentLOD == lodLevel) return;
            _currentLOD = lodLevel;

            bool enableMesh = (lodLevel == 0);
            bool enableLine = (lodLevel == 1);
            bool enablePhysics = (lodLevel != 3);

            // Mesh Renderer
            if (HighQualityRenderer != null)
            {
                if (HighQualityRenderer.enabled != enableMesh) HighQualityRenderer.enabled = enableMesh;
                var mr = HighQualityRenderer.GetComponent<MeshRenderer>();
                if (mr != null && mr.enabled != enableMesh) mr.enabled = enableMesh;
            }

            // Line Renderer
            if (LowQualityRenderer != null)
            {
                if (LowQualityRenderer.enabled != enableLine)
                {
                    LowQualityRenderer.enabled = enableLine;
                    if (enableLine) UpdateLineRendererGeometry();
                }
            }

            // Physics Toggle
            if (_ropeController != null)
            {
                if (_ropeController.enabled != enablePhysics)
                {
                    _ropeController.enabled = enablePhysics;
                }

                // Physics Quality (Substeps)
                if (enablePhysics)
                {
                    int targetSubsteps = LOD0_Substeps;
                    switch (lodLevel)
                    {
                        case 0: targetSubsteps = LOD0_Substeps; break;
                        case 1: targetSubsteps = LOD1_Substeps; break;
                        case 2: targetSubsteps = LOD2_Substeps; break;
                    }
                    
                    // Only apply if changed to avoid unnecessary property setting overhead
                    if (_ropeController.Settings.SubSteps != targetSubsteps)
                    {
                        _ropeController.Settings.SubSteps = targetSubsteps;
                    }
                }
            }
        }

        private void LateUpdate()
        {
            if (_currentLOD == 1 && _isVisible && LowQualityRenderer != null && LowQualityRenderer.enabled)
            {
                UpdateLineRendererGeometry();
            }
        }

        private void UpdateLineRendererGeometry()
        {
            if (_ropeController == null || !_ropeController.Solver.IsInitialized) return;
            
            int count = _ropeController.ParticleCount;
            if (_linePositionsBuffer == null || _linePositionsBuffer.Length != count) 
                _linePositionsBuffer = new Vector3[count];

            var nativePos = _ropeController.Solver.Positions;
            for (int i = 0; i < count; i++) _linePositionsBuffer[i] = nativePos[i];

            LowQualityRenderer.positionCount = count;
            LowQualityRenderer.SetPositions(_linePositionsBuffer);
        }

        private void InitializeLineRenderer()
        {
            if (LowQualityRenderer == null)
            {
                LowQualityRenderer = GetComponentInChildren<LineRenderer>(true);
                if (LowQualityRenderer == null)
                {
                    GameObject lrObj = new GameObject("LOD_LineRenderer");
                    lrObj.transform.SetParent(transform, false);
                    LowQualityRenderer = lrObj.AddComponent<LineRenderer>();
                }
            }
            LowQualityRenderer.enabled = false;
            LowQualityRenderer.useWorldSpace = true;
            LowQualityRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            LowQualityRenderer.receiveShadows = false;

            if (HighQualityRenderer != null)
            {
                float width = HighQualityRenderer.Radius * 2.0f;
                LowQualityRenderer.startWidth = width;
                LowQualityRenderer.endWidth = width;
                var meshRenderer = HighQualityRenderer.GetComponent<MeshRenderer>();
                if (meshRenderer != null && meshRenderer.sharedMaterial != null && LowQualityRenderer.sharedMaterial == null)
                {
                    Material sourceMat = meshRenderer.sharedMaterial;
                    Material lineMat = new Material(Shader.Find("Sprites/Default"));
                    if (sourceMat.HasProperty("_BaseColor")) lineMat.color = sourceMat.GetColor("_BaseColor");
                    else if (sourceMat.HasProperty("_Color")) lineMat.color = sourceMat.GetColor("_Color");
                    LowQualityRenderer.sharedMaterial = lineMat;
                }
            }
        }
    }
}