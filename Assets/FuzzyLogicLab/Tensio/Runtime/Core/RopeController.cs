using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
#if TENSIO_SPLINES_SUPPORT
using UnityEngine.Splines;
#endif
using Unity.Burst;

namespace Tensio
{
    /// <summary>
    /// Controls the XPBD (Extended Position Based Dynamics) rope simulation.
    /// Manages initialization, configuration, and the connection between Unity's Transform/Rigidbody world and the Burst-compiled solver.
    /// </summary>
    [DefaultExecutionOrder(200)]
    [AddComponentMenu("Tensio/Rope Controller")]
    public class RopeController : MonoBehaviour
    {
        #region Configuration Classes

        [Serializable]
        public class RopeInitSettings
        {
            public enum RopeType
            {
                Elastic,
                Chain
            }

            public enum DebrisMode
            {
                None,
                ShrinkAndDestroy,
                ConvertToRigidbody
            }

            [Tooltip("The behavior type of the rope. Elastic behaves like a rubber band, Chain is rigid.")]
            public RopeType Type = RopeType.Elastic;

            [Tooltip("Number of particles in the rope. Higher counts increase resolution but cost more performance.")]
            [Range(2, 200)] 
            public int ParticleCount = 20;

            [Tooltip("The total resting length of the rope.")]
            [Min(0.1f)] 
            public float Length = 5.0f;

            [Tooltip("If true, rope length is automatically synchronized with the curve (Bezier / Spline) length.")]
            public bool AutoCalculateLength = true;

            [Tooltip("If true, the first particle is automatically pinned to this Transform on start.")]
            public bool PinFirstParticle = true;

            [Tooltip("If true, the GameObject's position will match the first particle's position every frame. Useful when the rope moves away from its starting location.")]
            public bool FollowFirstParticle = false;

            [Tooltip("If true, the last particle connects back to the first, forming a closed loop.")]
            public bool IsClosedLoop = false;        }

        [Serializable]
        public class RopePhysicsSettings
        {
            [Tooltip("Global gravity vector applied to the rope.")]
            public Vector3 Gravity = new Vector3(0, -9.81f, 0);

            [Tooltip("Simulation substeps per frame. Higher values increase stiffness accuracy and stability.")]
            [Range(1, 30)] 
            public int SubSteps = 8;

            [Tooltip("Inverse stiffness. 0 is infinitely stiff (rigid). Higher values make the rope stretchier.")]
            [Min(0)] 
            public float Compliance = 0.0f;

            [Tooltip("Linear velocity damping. 0 is no damping, 1 is full stop. Helps stabilize the rope.")]
            [Range(0f, 1f)]
            public float LinearDamping = 0.01f;

            [Tooltip("If true, rendering will interpolate between physics steps for smoother motion at high framerates.")]
            public bool Interpolate = true;

            [Tooltip("Resistance to bending. 0 is perfectly flexible, 1 is highly rigid.")]
            [Range(0f, 1f)]
            public float BendingStiffness = 0.1f;

            [Tooltip("Friction coefficient for collisions.")]
            [Range(0f, 1f)] 
            public float Friction = 0.2f;

            [Tooltip("Radius of individual particles for collision detection.")]
            [Min(0.01f)] 
            public float ParticleRadius = 0.05f;

            [Tooltip("Mass of each particle. Affects inertia and interaction with dynamic bodies.")]
            [Min(0.01f)] 
            public float ParticleMass = 1.0f;

            [Tooltip("Maximum velocity a particle can reach. Prevents 'explosions' during rapid movement.")]
            [Min(0f)]
            public float MaxVelocity = 100f;

            [Tooltip("If a pinned particle moves more than this distance in one frame, it teleports (velocity reset).")]
            [Min(0f)]
            public float TeleportThreshold = 0.5f;

            [Tooltip("Fictitious Mass Scaling (e.g., 1.1) to make the top of the chain heavier than the bottom. Speeds up convergence for hanging chains. 1.0 = Off.")]
            [Min(1.0f)]
            public float MassScale = 1.0f;

            [Header("Breaking")]
            [Tooltip("If true, rope will break when stretched beyond the limit.")]
            public bool BreakOnStretch = false;

            [Tooltip("Ratio of segment length at which break occurs (e.g., 1.2 = 20% stretch).")]
            public float MaxStretchRatio = 1.2f;

            [Tooltip("If -1, breaks at max stretch. If >= 0, forces break check at this specific index.")]
            public int BreakIndexOverride = -1;

            [Tooltip("Time in seconds after spawning before breaking is enabled.")]
            public float BreakWarmup = 1.0f;

            [Header("Cutting")]
            [Tooltip("Minimum length a rope piece must have after a cut. Cuts resulting in smaller pieces will be ignored to prevent simulation errors.")]
            [Min(0f)]
            public float MinCutLength = 0.05f;
            
            [Header("Simulation")]
            [Tooltip("Number of stabilization steps to run on start (high damping, no gravity).")]
            public int WarmupSteps = 0;
            
            [Tooltip("If true, physics simulation is skipped and the rope follows the Bezier curve exactly.")]
            public bool IsStatic = false;

            [Tooltip("Layers to collide with (World Mesh Collision via SphereCast).")]
            public LayerMask CollisionMask = 0;

            [Tooltip("If true, this rope will interact with other ropes using the global spatial hash.")]
            public bool SupportInterRopeCollision = false;

            [Tooltip("Stiffness of the inter-rope collision response. Lower values (0.1-0.5) are softer and more stable.")]
            [Range(0.01f, 1.0f)]
            public float InterRopeStiffness = 0.5f;

            [Header("Proxy Collision (Two-Way)")]
            [Tooltip("If enabled, proxies hitting the rope will receive reactionary forces.")]
            public bool EnableTwoWayCoupling = false;

            [Tooltip("Multiplier for the reactionary force applied to rigidbodies.")]
            public float TwoWayCouplingForce = 1.0f;

            [Header("Visual Feedback")]
            [Tooltip("Visualize tension by changing color?")]
            public bool VisualizeStretch = true;
            
            [Tooltip("Color to tint the rope when under maximum stress.")]
            public Color StretchColor = Color.red;
            
            [Tooltip("How thin the rope visual gets when stretched (0.5 = 50% radius).")]
            [Range(0.1f, 1f)] 
            public float MaxThinningScale = 0.5f;

            [Header("Debris Management")]
            [Tooltip("What happens to cut pieces that have no pins attached?")]
            public RopeInitSettings.DebrisMode UnpinnedDebrisMode = RopeInitSettings.DebrisMode.None;

            [Tooltip("Time in seconds before the unpinned debris is destroyed.")]
            public float DebrisLifetime = 3.0f;

            [Tooltip("Time in seconds that the debris will take to shrink down before being destroyed.")]
            public float DebrisFadeTime = 1.0f;
        }

        [Serializable]
        public class AerodynamicsSettings
        {
            [Tooltip("Direction of the wind force.")]
            public Vector3 WindDirection = new Vector3(1, 0, 0);
            
            [Tooltip("Strength of the steady wind force.")]
            public float WindSpeed = 0.0f;
            
            [Tooltip("Amplitude of random turbulence.")]
            public float TurbulenceStrength = 0.0f;
            
            [Tooltip("Frequency of turbulence noise.")]
            public float TurbulenceFrequency = 0.5f;
            
            [Tooltip("Air resistance. Slows down the rope's movement.")]
            [Range(0f, 5f)] 
            public float AirDrag = 0.1f;
        }

        [Serializable]
        public class PinConstraint
        {
            [Tooltip("Index of the particle to pin (0 to ParticleCount-1).")]
            public int ParticleIndex;
            
            [Tooltip("The Transform to attach this particle to.")]
            public Transform Target;
            
            [Tooltip("Offset relative to the Target transform.")]
            public Vector3 LocalOffset;

            [Tooltip("If true, the Rope will take full control of this Rigidbody (Kinematic).")]
            public bool UseKinematicCoupling = false; 

            [HideInInspector] public Rigidbody CachedBody;
            [HideInInspector] public bool WasKinematic; 
        }

        #endregion

        // ---------------------------------------------------------------------

        #region Serialized Fields

        public enum RopeInitMode { Bezier
#if TENSIO_SPLINES_SUPPORT
        , Spline 
#endif
        }

        [HideInInspector]
        public RopeInitMode InitMode = RopeInitMode.Bezier;
        
#if TENSIO_SPLINES_SUPPORT
        [Header("Spline Support")]
        public SplineContainer SplineReference; 
        
        [Tooltip("Indices of the spline knots to pin.")]
        public List<int> PinnedSplineKnots = new List<int>(); 
#endif

        [Header("Configuration")] 
        [SerializeField]
        private RopeInitSettings _initSettings;

        [SerializeField] 
        private RopePhysicsSettings _physicsSettings;
        
        [SerializeField] 
        private AerodynamicsSettings _aeroSettings;

        [Header("Constraints")] 
        [SerializeField]
        private List<PinConstraint> _pinConstraints = new List<PinConstraint>();

        #endregion

        #region Bezier Curve Support
        
        public enum BezierControlMode { Free, Aligned, Mirrored }

        [System.Serializable]
        public class BezierPoint
        {
            public Vector3 Position;
            public Vector3 TangentIn;  // Relative to Position
            public Vector3 TangentOut; // Relative to Position
            public BezierControlMode Mode = BezierControlMode.Mirrored;
            
            public bool IsPinned = false;
            public Transform PinTarget;
            public Vector3 PinOffset;

            public BezierPoint(Vector3 pos, Vector3 tanIn, Vector3 tanOut)
            {
                Position = pos;
                TangentIn = tanIn;
                TangentOut = tanOut;
                Mode = BezierControlMode.Mirrored;
                IsPinned = false;
                PinTarget = null;
                PinOffset = Vector3.zero;
            }
        }

        [Header("Shape Editing")]
        [Tooltip("If true, the initial shape is defined by the Bezier control points below.")]
        public bool UseBezierCurve = true;
        
        public List<BezierPoint> ControlPoints = new List<BezierPoint>();
        
        // Cache for Editor Visualization and Initialization
        [HideInInspector]
        public Vector3[] EditorPreviewPositions; 
        
        #endregion

        #region Public Properties

        // Accessors for external scripts (maintaining references to the setting objects)
        public RopeInitSettings InitSettings => _initSettings;
        public RopePhysicsSettings PhysicsSettings => _physicsSettings;
        public AerodynamicsSettings AeroSettings => _aeroSettings;
        public List<PinConstraint> PinConstraints => _pinConstraints;

        public event Action<Vector3, float> OnCollisionImpulse;
        public event Action OnRopeCut;

        public RopeSolver Solver => _solver;
        public RopePhysicsSettings Settings => _physicsSettings;
        public float CurrentLength => _initSettings.Length;
        public int ParticleCount => _initSettings.ParticleCount;
        /// <summary>
        /// Unique integer identifier for this rope instance, used for solver collision filtering.
        /// </summary>
        public int RopeId
        {
            get
            {
#if UNITY_6000_4_OR_NEWER
                int id = GetEntityId().GetHashCode();
                return id != 0 ? id : 1;
#else
                return GetInstanceID();
#endif
            }
        }
        public float CurrentStressRatio { get; private set; }
        public float AverageVelocity { get; private set; }
        public float InterpolationFactor { get; private set; }
        
        public NativeArray<float3> Positions => _solver.Positions;
        public NativeArray<float3> Velocities => _solver.Velocities;

        /// <summary>
        /// Calculates interpolated positions and snaps pinned particles to their targets' current positions.
        /// Prevents 'ghosting' or trailing lag where the rope lags behind fast-moving bones.
        /// </summary>
        public void GetVisualPositions(NativeArray<float3> outBuffer)
        {
            if (_solver == null || !_solver.IsInitialized) return;

            var current = _solver.Positions;
            var last = _solver.LastFramePositions;
            float t = InterpolationFactor;

            for (int i = 0; i < current.Length; i++)
            {
                outBuffer[i] = math.lerp(last[i], current[i], t);
            }

            // Snap pins to current transform positions
            if (_physicsSettings.Interpolate)
            {
                foreach (var pin in _pinConstraints)
                {
                    if (pin.Target != null)
                    {
                        outBuffer[pin.ParticleIndex] = pin.Target.TransformPoint(pin.LocalOffset);
                    }
                }
            }
        }

        public static readonly List<RopeController> AllRopes = new List<RopeController>();

        #endregion

        #region Private Fields

        // Internal State
        private RopeSolver _solver;
        private float _enableBreakTime;

        // Buffers
        private PinConstraintData[] _pinBuffer;

        #endregion

        #region Unity Lifecycle

        private void Reset()
        {
            if (_initSettings == null) _initSettings = new RopeInitSettings();
            if (_physicsSettings == null) _physicsSettings = new RopePhysicsSettings();
            if (_aeroSettings == null) _aeroSettings = new AerodynamicsSettings();
            if (_pinConstraints == null) _pinConstraints = new List<PinConstraint>();

            if (ControlPoints.Count == 0)
            {
                InitializeBezierCurve();
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!Application.isPlaying && _initSettings != null)
            {
                if (InitMode == RopeInitMode.Bezier && UseBezierCurve && ControlPoints != null && ControlPoints.Count >= 2)
                {
                    if (EditorPreviewPositions == null || EditorPreviewPositions.Length != _initSettings.ParticleCount)
                    {
                        RefreshParticlesFromBezier();
                    }
                }
#if TENSIO_SPLINES_SUPPORT
                else if (InitMode == RopeInitMode.Spline && SplineReference != null && SplineReference.Spline != null)
                {
                    RefreshParticlesFromSpline();
                }
#endif
            }
        }
#endif

        private void Awake()
        {
            if (_initSettings == null) _initSettings = new RopeInitSettings();
            if (_physicsSettings == null) _physicsSettings = new RopePhysicsSettings();
            if (_aeroSettings == null) _aeroSettings = new AerodynamicsSettings();
            if (_pinConstraints == null) _pinConstraints = new List<PinConstraint>();
        }

        private void OnEnable()
        {
            AllRopes.Add(this);
            if (RopeInteractionManager.Instance != null) RopeInteractionManager.Instance.Register(this);
        }

        private void OnDisable()
        {
            AllRopes.Remove(this);
            if (RopeInteractionManager.Instance != null) RopeInteractionManager.Instance.Unregister(this);
        }

        private void Start()
        {
            if (RopeInteractionManager.Instance == null)
            {
                
            }
            else
            {
                RopeInteractionManager.Instance.Register(this);
            }

            if (_solver == null) InitializeRope();
        }

        private void OnDestroy()
        {
            if (RopeInteractionManager.Instance != null)
            {
                RopeInteractionManager.Instance.GlobalMapDependency.Complete();
            }

            _solver?.Dispose();
            _solver = null;
        }

        private void Update()
        {
            if (_solver != null && _solver.IsInitialized && _initSettings.FollowFirstParticle)
            {
                transform.position = _solver.GetPosition(0);
            }
        }

        private void LateUpdate()
        {
            if (_physicsSettings.Interpolate)
            {
                float t = (Time.time - Time.fixedTime) / Time.fixedDeltaTime;
                InterpolationFactor = Mathf.Clamp01(t);
            }
            else
            {
                InterpolationFactor = 1.0f;
            }
        }

        /// <summary>
        /// Gets the current job handle for the rope simulation.
        /// </summary>
        public JobHandle GetSolverHandle() => _solver != null ? _solver.GetJobHandle() : default;

        private void FixedUpdate()
        {
            if (_solver == null) return;

            // --- STATIC MODE ---
            if (_physicsSettings.IsStatic)
            {
                if (InitMode == RopeInitMode.Bezier && ControlPoints.Count >= 2)
                {
                    var curve = SampleBezierCurve(_initSettings.ParticleCount, out float len);
                    if (_initSettings.AutoCalculateLength) _initSettings.Length = len;
                    if (_solver.Positions.Length == curve.Length)
                    {
                        _solver.Positions.CopyFrom(curve);
                        _solver.PrevPositions.CopyFrom(curve); 
                    }
                }
#if TENSIO_SPLINES_SUPPORT
                else if (InitMode == RopeInitMode.Spline && SplineReference != null && SplineReference.Spline != null)
                {
                    var curve = SampleSpline(_initSettings.ParticleCount, out float len);
                    if (_initSettings.AutoCalculateLength) _initSettings.Length = len;
                    if (_solver.Positions.Length == curve.Length)
                    {
                        _solver.Positions.CopyFrom(curve);
                        _solver.PrevPositions.CopyFrom(curve);
                    }
                }
#endif
                return;
            }

            NativeArray<SphereColliderData> sphereNative = default;
            NativeArray<PlaneColliderData> planeNative = default;
            NativeArray<CapsuleColliderData> capsuleNative = default;
            NativeArray<BoxColliderData> boxNative = default;

            RopeCollisionManager.GetSphereData(ref sphereNative, Allocator.TempJob);
            RopeCollisionManager.GetPlaneData(ref planeNative, Allocator.TempJob);
            RopeCollisionManager.GetCapsuleData(ref capsuleNative, Allocator.TempJob);
            RopeCollisionManager.GetBoxData(ref boxNative, Allocator.TempJob);

            if (RopeInteractionManager.Instance == null || !RopeInteractionManager.Instance.enabled)
            {
                if (RopeInteractionManager.Instance != null)
                {
                    RopeInteractionManager.Instance.GlobalMapDependency.Complete();
                }
            }

            PreparePinDataAndHandleCoupling();

            _solver.UpdateColliders(sphereNative, planeNative, capsuleNative, boxNative);
            
            if (sphereNative.IsCreated) sphereNative.Dispose();
            if (planeNative.IsCreated) planeNative.Dispose();
            if (capsuleNative.IsCreated) capsuleNative.Dispose();
            if (boxNative.IsCreated) boxNative.Dispose();

            _solver.UpdatePinConstraints(_pinBuffer);

            // --- MANAGED STEPPING ---
            if (RopeInteractionManager.Instance != null && RopeInteractionManager.Instance.enabled)
            {
                return;
            }

            // --- INDEPENDENT STEPPING (Fallback) ---
            float dt = Time.fixedDeltaTime;
            float compliance = (_initSettings.Type == RopeInitSettings.RopeType.Chain)
                ? 0.0f
                : _physicsSettings.Compliance / 10000f;

            float3 windVel = (_aeroSettings.WindSpeed > 0)
                ? ((Vector3)_aeroSettings.WindDirection).normalized * _aeroSettings.WindSpeed
                : Vector3.zero;

            var globalMap = (RopeInteractionManager.Instance != null)
                ? RopeInteractionManager.Instance.GetGlobalMap()
                : default;
            
            float globalCellSize = (RopeInteractionManager.Instance != null) ? RopeInteractionManager.Instance.CellSize : 0f;
            JobHandle globalDep = (RopeInteractionManager.Instance != null) ? RopeInteractionManager.Instance.GlobalMapDependency : default;

            _solver.Step(dt, _physicsSettings.SubSteps, _physicsSettings.Gravity, compliance, _physicsSettings.Friction,
                _physicsSettings.ParticleRadius, _physicsSettings.LinearDamping, _physicsSettings.BendingStiffness,
                windVelocity: windVel, turbulenceStrength: _aeroSettings.TurbulenceStrength, turbulenceFrequency: _aeroSettings.TurbulenceFrequency, airDrag: _aeroSettings.AirDrag,
                time: Time.time, globalSpatialMap: globalMap, globalCellSize: globalCellSize, interRopeStiffness: _physicsSettings.InterRopeStiffness, collisionLayer: _physicsSettings.CollisionMask, IsClosedLoop: _initSettings.IsClosedLoop, 
                maxVelocity: _physicsSettings.MaxVelocity, massScale: _physicsSettings.MassScale, externalDependency: globalDep, applyTwoWayCoupling: _physicsSettings.EnableTwoWayCoupling);

            FinalizeFrame();
        }

        /// <summary>
        /// Finalizes the simulation frame. Called either internally or by the RopeInteractionManager.
        /// </summary>
        public void FinalizeFrame()
        {
            if (_solver == null || !_solver.IsInitialized) return;

            bool canBreak = _physicsSettings.BreakOnStretch && Time.time > _enableBreakTime;
            if (canBreak) _solver.ScheduleBreakCheck(_physicsSettings.MaxStretchRatio, _physicsSettings.BreakIndexOverride);

            _solver.CompleteJobs();

            ProcessStatsAndEvents();

            if (canBreak)
            {
                int breakIdx = _solver.BreakIndex;
                if (breakIdx != -1) Split(breakIdx, 0.5f);
            }

            ApplyPinForces();
            ApplyKinematicTransforms();
        }

        private void OnDrawGizmos()
        {
            if (_solver != null && _solver.IsInitialized)
            {
                Gizmos.color = Color.white;
                for (int i = 0; i < _initSettings.ParticleCount; i++)
                {
                    Gizmos.DrawSphere(_solver.Positions[i], _physicsSettings.ParticleRadius);
                }
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Initializes the rope solver with current settings.
        /// Re-creates the solver if it already exists.
        /// </summary>
        public void InitializeRope()
        {
            _solver?.Dispose();

            // 1. Resolve Initial Shape & Length
            float3[] initialPositions = null;
            
#if TENSIO_SPLINES_SUPPORT
            if (InitMode == RopeInitMode.Spline)
            {
                RegeneratePinsFromSpline();
                if (SplineReference != null && SplineReference.Spline != null)
                {
                    initialPositions = SampleSpline(_initSettings.ParticleCount, out float curveLen);
                    if (_initSettings.AutoCalculateLength) _initSettings.Length = curveLen;
                }
            }
            if (initialPositions == null)
#else
            if (true)
#endif
            {
                // Prefer baked EditorPreviewPositions for instant, zero-pop startup if they match the particle count perfectly
                if (EditorPreviewPositions != null && EditorPreviewPositions.Length == _initSettings.ParticleCount)
                {
                    float3 spawnOffset = Application.isPlaying ? ((float3)transform.position - (float3)EditorPreviewPositions[0]) : float3.zero;
                    initialPositions = new float3[_initSettings.ParticleCount];
                    for (int i = 0; i < _initSettings.ParticleCount; i++)
                    {
                        initialPositions[i] = (float3)EditorPreviewPositions[i] + spawnOffset;
                    }
                }
                else if (InitMode == RopeInitMode.Bezier && UseBezierCurve && ControlPoints.Count >= 2)
                {
                    initialPositions = SampleBezierCurve(_initSettings.ParticleCount, out float curveLen);
                    
                    // Auto-calculate length from the curve to prevent constraint fighting
                    if (_initSettings.AutoCalculateLength) _initSettings.Length = curveLen;
                }
            }

            // 2. Create Solver
            _solver = new RopeSolver(_initSettings.ParticleCount, _initSettings.Length, transform.position, RopeId);
            _enableBreakTime = Time.time + _physicsSettings.BreakWarmup;

            // 3. Apply Initial State
            if (initialPositions != null)
            {
                _solver.Positions.CopyFrom(initialPositions);
                _solver.PrevPositions.CopyFrom(initialPositions);
            }

            // Apply configured mass
            float initInvMass = 1.0f / Mathf.Max(0.001f, _physicsSettings.ParticleMass);
            var masses = _solver.InverseMasses;
            for (int i = 0; i < masses.Length; i++) masses[i] = initInvMass;

#if TENSIO_SPLINES_SUPPORT
            if (InitMode == RopeInitMode.Spline)
            {
                // In Spline mode, pinning is strictly managed by PinnedSplineKnots via RegeneratePinsFromSpline().
                // If knot 0 is not in PinnedSplineKnots, ensure particle 0 is NOT pinned to transform.
                if (PinnedSplineKnots == null || !PinnedSplineKnots.Contains(0))
                {
                    _pinConstraints.RemoveAll(p => p.ParticleIndex == 0 && p.Target == transform);
                }
            }
            else
#endif
            if (InitMode == RopeInitMode.Bezier && UseBezierCurve && ControlPoints.Count >= 2)
            {
                // In Bezier mode, pinning is driven by ControlPoints[0].IsPinned.
                if (ControlPoints.Count > 0 && !ControlPoints[0].IsPinned)
                {
                    _pinConstraints.RemoveAll(p => p.ParticleIndex == 0 && p.Target == transform);
                }
            }
            else
            {
                if (_initSettings.PinFirstParticle)
                {
                    if (_pinConstraints.Find(p => p.ParticleIndex == 0) == null)
                    {
                        PinParticle(0, transform, transform.position);
                    }
                }
                else
                {
                    // Remove auto-pin if it exists
                    _pinConstraints.RemoveAll(p => p.ParticleIndex == 0 && p.Target == transform);
                }
            }

            if (_initSettings.Type == RopeInitSettings.RopeType.Chain)
            {
                GenerateChainTethers();
            }
            
            // 4. Stabilize
            if (!_physicsSettings.IsStatic) WarmupRope();
        }

        public void SetVelocities(int index, Vector3 vel) => _solver.SetVelocities(index, vel);

        /// <summary>
        /// Gets the current world position of a particle.
        /// </summary>
        public Vector3 GetParticlePosition(int index)
        {
            if (_solver == null || !_solver.IsInitialized) return Vector3.zero;
            return _solver.GetPosition(index);
        }

        /// <summary>
        /// Gets the current velocity of a particle.
        /// </summary>
        public Vector3 GetParticleVelocity(int index)
        {
            if (_solver == null || !_solver.IsInitialized) return Vector3.zero;
            return _solver.GetVelocity(index);
        }

        /// <summary>
        /// Adds a physical force to a specific particle, similar to Rigidbody.AddForce.
        /// </summary>
        public void AddForce(int index, Vector3 force)
        {
            if (_solver == null || !_solver.IsInitialized) return;
            _solver.AddForce(index, force, Time.fixedDeltaTime);
        }

        /// <summary>
        /// Adds a uniform physical force to all particles in the rope.
        /// </summary>
        public void AddForceToAll(Vector3 force)
        {
            if (_solver == null || !_solver.IsInitialized) return;
            for (int i = 0; i < ParticleCount; i++)
            {
                _solver.AddForce(i, force, Time.fixedDeltaTime);
            }
        }

        /// <summary>
        /// Applies an explosion force to the rope, pushing particles away from the center.
        /// </summary>
        public void AddExplosionForce(float explosionForce, Vector3 explosionPosition, float explosionRadius)
        {
            if (_solver == null || !_solver.IsInitialized) return;
            float radiusSq = explosionRadius * explosionRadius;
            
            for (int i = 0; i < ParticleCount; i++)
            {
                Vector3 pPos = _solver.GetPosition(i);
                Vector3 dir = pPos - explosionPosition;
                float distSq = dir.sqrMagnitude;
                
                if (distSq < radiusSq)
                {
                    float dist = Mathf.Sqrt(distSq);
                    float falloff = 1.0f - (dist / explosionRadius);
                    Vector3 force = (dir.normalized * explosionForce) * falloff;
                    _solver.AddForce(i, force, Time.fixedDeltaTime);
                }
            }
        }

        /// <summary>
        /// Unpins a previously pinned particle.
        /// </summary>
        public void UnpinParticle(int index)
        {
            int removed = _pinConstraints.RemoveAll(p => p.ParticleIndex == index);
            if (removed > 0 && _solver != null && _solver.IsInitialized)
            {
                // Re-prepare pins and update solver
                PreparePinDataAndHandleCoupling();
                _solver.UpdatePinConstraints(_pinBuffer);
            }
        }

        /// <summary>
        /// Removes all pins from this rope.
        /// </summary>
        public void UnpinAll()
        {
            if (_pinConstraints.Count > 0)
            {
                _pinConstraints.Clear();
                if (_solver != null && _solver.IsInitialized)
                {
                    PreparePinDataAndHandleCoupling();
                    _solver.UpdatePinConstraints(_pinBuffer);
                }
            }
        }

        /// <summary>
        /// Pins a particle to a specific Transform at a world position.
        /// </summary>
        public void PinParticle(int index, Transform target, Vector3 worldPos)
        {
            var existing = _pinConstraints.Find(p => p.ParticleIndex == index);
            if (existing != null)
            {
                existing.Target = target;
                existing.LocalOffset = target.InverseTransformPoint(worldPos);
                existing.CachedBody = target.GetComponent<Rigidbody>();
            }
            else
            {
                _pinConstraints.Add(new PinConstraint
                {
                    ParticleIndex = index,
                    Target = target,
                    LocalOffset = target.InverseTransformPoint(worldPos),
                    CachedBody = target.GetComponent<Rigidbody>()
                });
            }
        }

        public void SetCurrentLength(float length)
        {
            _initSettings.Length = Mathf.Max(0.1f, length);
            _solver?.SetRopeLength(_initSettings.Length);
        }

        /// <summary>
        /// Gradually changes the length of the rope by the specified delta amount. 
        /// Ideal for winches, cranes, or reeling in a grappling hook.
        /// </summary>
        public void ChangeLength(float delta)
        {
            SetCurrentLength(_initSettings.Length + delta);
        }

        /// <summary>
        /// Splits the rope at the given segment index and fraction, creating a new RopeController for the cut-off part.
        /// </summary>
        public void Split(int segmentIndex, float segmentFraction)
        {
            if (_solver == null || segmentIndex < 0 || segmentIndex >= _initSettings.ParticleCount - 1) return;

            var pos = _solver.Positions.ToArray();
            var prev = _solver.PrevPositions.ToArray();
            var vel = _solver.Velocities.ToArray();
            var invMass = _solver.InverseMasses.ToArray();

            // Calculate the exact cut point
            float3 cutPos = math.lerp(pos[segmentIndex], pos[segmentIndex + 1], segmentFraction);
            float3 cutPrev = math.lerp(prev[segmentIndex], prev[segmentIndex + 1], segmentFraction);
            float3 cutVel = math.lerp(vel[segmentIndex], vel[segmentIndex + 1], segmentFraction);
            float cutInvMass = math.lerp(invMass[segmentIndex], invMass[segmentIndex + 1], segmentFraction);

            int countA = segmentIndex + 2;
            int countB = _initSettings.ParticleCount - segmentIndex;

            // A physical rope piece requires at least 2 particles to exist.
            // To prevent the rope from simply evaporating/melting when the user continuously cuts the ends,
            // we reject cuts that would produce non-simulatable fragments.
            if (countA < 2 || countB < 2) return;

            // Prepare A
            var posA = new float3[countA];
            var prevA = new float3[countA];
            var velA = new float3[countA];
            var invMassA = new float[countA];
            Array.Copy(pos, 0, posA, 0, segmentIndex + 1);
            Array.Copy(prev, 0, prevA, 0, segmentIndex + 1);
            Array.Copy(vel, 0, velA, 0, segmentIndex + 1);
            Array.Copy(invMass, 0, invMassA, 0, segmentIndex + 1);
            posA[countA - 1] = cutPos;
            prevA[countA - 1] = cutPrev;
            velA[countA - 1] = cutVel;
            invMassA[countA - 1] = cutInvMass;

            // Prepare B
            var posB = new float3[countB];
            var prevB = new float3[countB];
            var velB = new float3[countB];
            var invMassB = new float[countB];
            posB[0] = cutPos;
            prevB[0] = cutPrev;
            velB[0] = cutVel;
            invMassB[0] = cutInvMass;
            Array.Copy(pos, segmentIndex + 1, posB, 1, countB - 1);
            Array.Copy(prev, segmentIndex + 1, prevB, 1, countB - 1);
            Array.Copy(vel, segmentIndex + 1, velB, 1, countB - 1);
            Array.Copy(invMass, segmentIndex + 1, invMassB, 1, countB - 1);

            float originalSegmentLength = _initSettings.ParticleCount > 1 ? _initSettings.Length / (_initSettings.ParticleCount - 1) : 0;
            float lengthA = (segmentIndex + segmentFraction) * originalSegmentLength;
            float lengthB = (_initSettings.ParticleCount - 1 - segmentIndex - segmentFraction) * originalSegmentLength;

            // Enforce minimum cut length to prevent simulation errors with tiny fragments
            if (lengthA < _physicsSettings.MinCutLength || lengthB < _physicsSettings.MinCutLength) return;

            PerformExactSplitLogic(posA, prevA, velA, invMassA, lengthA, posB, prevB, velB, invMassB, lengthB, segmentIndex);
        }

        /// <summary>
        /// Helper function to split the rope at the closest point to a given world position.
        /// Useful for sword slashes or projectiles where you only know the impact location.
        /// </summary>
        public void SplitAtWorldPosition(Vector3 worldPos)
        {
            if (_solver == null || !_solver.IsInitialized) return;

            int bestSegment = -1;
            float bestFraction = 0.5f;
            float minSqrDist = float.MaxValue;
            Vector3 floatPos = worldPos;

            for (int i = 0; i < ParticleCount - 1; i++)
            {
                Vector3 p0 = _solver.GetPosition(i);
                Vector3 p1 = _solver.GetPosition(i + 1);
                
                // Find closest point on line segment
                Vector3 lineDir = p1 - p0;
                float sqrLen = lineDir.sqrMagnitude;
                if (sqrLen < 0.0001f) continue;
                
                float t = Mathf.Clamp01(Vector3.Dot(floatPos - p0, lineDir) / sqrLen);
                Vector3 closestPoint = p0 + t * lineDir;
                
                float sqrDist = (floatPos - closestPoint).sqrMagnitude;
                if (sqrDist < minSqrDist)
                {
                    minSqrDist = sqrDist;
                    bestSegment = i;
                    bestFraction = t;
                }
            }

            if (bestSegment != -1)
            {
                // Add a small threshold check to prevent splitting if the cut is incredibly far away?
                // For now, assume if this method is called, a cut *must* happen at the closest point.
                Split(bestSegment, bestFraction);
            }
        }

        /// <summary>
        /// Performs a raycast against the rope particles.
        /// </summary>
        public bool Raycast(Ray ray, out float hitDistance, out int segmentIndex, out float segmentFraction)
        {
            hitDistance = float.MaxValue;
            segmentIndex = -1;
            segmentFraction = 0f;
            
            if (_solver == null || !_solver.IsInitialized) return false;

            int segmentCount = _solver.Positions.Length - 1;
            NativeArray<float> results = new NativeArray<float>(segmentCount, Allocator.TempJob);
            NativeArray<float> fractions = new NativeArray<float>(segmentCount, Allocator.TempJob);

            new RopeRaycastJob
            {
                Positions = _solver.Positions,
                Origin = ray.origin,
                Direction = ray.direction,
                Radius = math.max(_physicsSettings.ParticleRadius, 0.05f),
                Results = results,
                SegmentFractions = fractions
            }.Schedule(segmentCount, 64).Complete();

            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] > 0 && results[i] < hitDistance)
                {
                    hitDistance = results[i];
                    segmentIndex = i;
                    segmentFraction = fractions[i];
                }
            }

            results.Dispose();
            fractions.Dispose();
            return segmentIndex != -1;
        }
        
        // Overload for backward compatibility if needed, though we change the signature to force update in RopeCutter
        public bool Raycast(Ray ray, out float hitDistance, out int segmentIndex)
        {
             return Raycast(ray, out hitDistance, out segmentIndex, out float _);
        }
        
        public void SetParticlePosition(int index, Vector3 pos)
        {
            _solver.SetParticlePosition(index, pos);
        }

        public void InitializeBezierCurve()
        {
            if (ControlPoints.Count == 0)
            {
                float ropeLength = _initSettings != null ? _initSettings.Length : 5.0f;
                bool pinFirst = _initSettings != null ? _initSettings.PinFirstParticle : true;

                var startPoint = new BezierPoint(transform.position, new Vector3(0, 1, 0), new Vector3(0, -1, 0))
                {
                    IsPinned = pinFirst,
                    PinTarget = pinFirst ? transform : null,
                    PinOffset = Vector3.zero
                };
                var endPoint = new BezierPoint(transform.position + Vector3.down * ropeLength, new Vector3(0, 1, 0), new Vector3(0, -1, 0));

                ControlPoints.Add(startPoint);
                ControlPoints.Add(endPoint);
            }
            RefreshParticlesFromBezier();
        }

        public void RefreshParticlesFromBezier()
        {
            if (ControlPoints.Count < 2) return;
            var sampled = SampleBezierCurve(_initSettings.ParticleCount, out float curveLen);
            if (_initSettings.AutoCalculateLength)
            {
                _initSettings.Length = curveLen;
            }
            EditorPreviewPositions = new Vector3[sampled.Length];
            for (int i = 0; i < sampled.Length; i++) EditorPreviewPositions[i] = sampled[i];
            
            RegeneratePinsFromControlPoints();
            SyncTransformToFirstParticle();
        }

#if TENSIO_SPLINES_SUPPORT
        public void RefreshParticlesFromSpline()
        {
            RegeneratePinsFromSpline();
            if (SplineReference == null || SplineReference.Spline == null) return;
            var sampled = SampleSpline(_initSettings.ParticleCount, out float curveLen);
            if (_initSettings.AutoCalculateLength)
            {
                _initSettings.Length = curveLen;
            }
            EditorPreviewPositions = new Vector3[sampled.Length];
            for (int i = 0; i < sampled.Length; i++) EditorPreviewPositions[i] = sampled[i];

            SyncTransformToFirstParticle();
        }
#endif

        public void RegeneratePinsFromControlPoints()
        {
            _pinConstraints.Clear();
            if (ControlPoints.Count == 0)
            {
                if (_initSettings != null) _initSettings.PinFirstParticle = false;
                return;
            }

            if (_initSettings != null) _initSettings.PinFirstParticle = ControlPoints[0].IsPinned;

            // Strategy: Map CP index to Particle Index based on arc length distribution
            List<float> cpFractions = GetControlPointFractions(); 
            
            for (int i = 0; i < ControlPoints.Count; i++)
            {
                var cp = ControlPoints[i];
                if (cp.IsPinned)
                {
                    // Find particle index
                    float t = cpFractions[i]; 
                    int pIdx = Mathf.RoundToInt(t * (_initSettings.ParticleCount - 1));
                    pIdx = Mathf.Clamp(pIdx, 0, _initSettings.ParticleCount - 1);
                    
                    Transform target = cp.PinTarget ? cp.PinTarget : transform;
                    Vector3 offset = cp.PinTarget ? cp.PinOffset : transform.InverseTransformPoint(cp.Position);
                    
                    // If default transform, ensure offset is correct relative to it
                    if (target == transform && cp.PinTarget == null)
                    {
                        offset = transform.InverseTransformPoint(cp.Position);
                    }
                    
                    _pinConstraints.Add(new PinConstraint
                    {
                         ParticleIndex = pIdx,
                         Target = target,
                         LocalOffset = offset,
                         CachedBody = target.GetComponent<Rigidbody>()
                    });
                }
            }
        }

#if TENSIO_SPLINES_SUPPORT
        public void RegeneratePinsFromSpline()
        {
            _pinConstraints.Clear();
            if (_initSettings != null) _initSettings.PinFirstParticle = PinnedSplineKnots != null && PinnedSplineKnots.Contains(0);
            if (SplineReference == null || SplineReference.Spline == null) return;
            var spline = SplineReference.Spline;
            if (spline.Count == 0) return;

            float totalLength = spline.GetLength();
            if (totalLength <= 0f) return;

            for (int i = 0; i < spline.Count; i++)
            {
                if (!PinnedSplineKnots.Contains(i)) continue;

                float accLen = 0f;
                for (int k = 0; k < i; k++) accLen += spline.GetCurveLength(k);
                
                float t = accLen / totalLength;
                int pIdx = Mathf.RoundToInt(t * (_initSettings.ParticleCount - 1));
                pIdx = Mathf.Clamp(pIdx, 0, _initSettings.ParticleCount - 1);
                
                Transform target = SplineReference.transform;
                Vector3 offset = spline[i].Position;
                
                _pinConstraints.Add(new PinConstraint
                {
                    ParticleIndex = pIdx,
                    Target = target,
                    LocalOffset = offset,
                    CachedBody = target.GetComponent<Rigidbody>()
                });
            }
        }
#endif

        private List<float> GetControlPointFractions()
        {
             List<float> fractions = new List<float>();
             fractions.Add(0f);
             if (ControlPoints.Count < 2) return fractions;

             float total = 0;
             List<float> segLens = new List<float>();
             int resolution = 10;
             
             for(int i=0; i<ControlPoints.Count-1; i++)
             {
                 float len = GetSegmentLength(i, resolution);
                 segLens.Add(len);
                 total += len;
             }
             
             if (total <= 0)
             {
                 for(int i=1; i<ControlPoints.Count; i++) fractions.Add(i / (float)(ControlPoints.Count-1));
                 return fractions;
             }

             float current = 0;
             for(int i=0; i<segLens.Count; i++)
             {
                 current += segLens[i];
                 fractions.Add(current / total);
             }
             
             // Force last to 1.0 (or close enough)
             if (fractions.Count < ControlPoints.Count) fractions.Add(1.0f);
             else fractions[fractions.Count-1] = 1.0f;
             
             return fractions;
        }

        public void RefreshParticlesFromPins()
        {
            if (_initSettings.ParticleCount < 2) return;

            if (EditorPreviewPositions == null || EditorPreviewPositions.Length != _initSettings.ParticleCount)
            {
                EditorPreviewPositions = new Vector3[_initSettings.ParticleCount];
            }

            // Define Key Nodes: Start, End, and Pins
            var keyNodes = new SortedDictionary<int, Vector3>();

            // Default Start
            keyNodes[0] = transform.position;

            // Default End
            keyNodes[_initSettings.ParticleCount - 1] = transform.position + Vector3.down * _initSettings.Length;

            // Overwrite with Pins
            foreach (var pin in _pinConstraints)
            {
                if (pin.Target != null && pin.ParticleIndex >= 0 && pin.ParticleIndex < _initSettings.ParticleCount)
                {
                    keyNodes[pin.ParticleIndex] = pin.Target.TransformPoint(pin.LocalOffset);
                }
            }

            // Interpolate
            var keys = new List<int>(keyNodes.Keys);
            for (int k = 0; k < keys.Count - 1; k++)
            {
                int idxA = keys[k];
                int idxB = keys[k + 1];
                Vector3 posA = keyNodes[idxA];
                Vector3 posB = keyNodes[idxB];

                int count = idxB - idxA;
                if (count > 0)
                {
                    for (int i = 0; i <= count; i++)
                    {
                        float t = i / (float)count;
                        EditorPreviewPositions[idxA + i] = Vector3.Lerp(posA, posB, t);
                    }
                }
            }
            SyncTransformToFirstParticle();
        }

        private void SyncTransformToFirstParticle()
        {
            if (!Application.isPlaying && _initSettings.FollowFirstParticle && EditorPreviewPositions != null && EditorPreviewPositions.Length > 0)
            {
                if (transform.position != EditorPreviewPositions[0])
                {
                    transform.position = EditorPreviewPositions[0];
                }
            }
        }

        /// <summary>
        /// Gets the current geometric length of the active curve (Spline or Bezier).
        /// </summary>
        public float GetCalculatedCurveLength()
        {
#if TENSIO_SPLINES_SUPPORT
            if (InitMode == RopeInitMode.Spline && SplineReference != null && SplineReference.Spline != null)
            {
                return SplineReference.Spline.GetLength();
            }
#endif
            if (InitMode == RopeInitMode.Bezier && ControlPoints != null && ControlPoints.Count >= 2)
            {
                SampleBezierCurve(Mathf.Max(10, _initSettings.ParticleCount * 2), out float len);
                return len;
            }

            return _initSettings != null ? _initSettings.Length : 5.0f;
        }

        /// <summary>
        /// Scales the curve geometry (Bezier control points or Spline knots) proportionally so its length matches targetLength.
        /// When pins are present (e.g. both ends pinned), pinned anchors remain fixed while the curve expands/sags between them.
        /// </summary>
        public void ScaleCurveToLength(float targetLength)
        {
            targetLength = Mathf.Max(0.1f, targetLength);
            _initSettings.Length = targetLength;

            if (InitMode == RopeInitMode.Bezier && UseBezierCurve && ControlPoints.Count >= 2)
            {
                ScaleBezierCurve(targetLength);
                RefreshParticlesFromBezier();
            }
#if TENSIO_SPLINES_SUPPORT
            else if (InitMode == RopeInitMode.Spline && SplineReference != null && SplineReference.Spline != null && SplineReference.Spline.Count >= 2)
            {
                ScaleSplineCurve(targetLength);
                RefreshParticlesFromSpline();
            }
#endif
        }

        private void ScaleBezierCurve(float targetLength)
        {
            if (ControlPoints == null || ControlPoints.Count < 2) return;

            // Find all pinned control point indices
            List<int> pinned = new List<int>();
            for (int i = 0; i < ControlPoints.Count; i++)
            {
                if (ControlPoints[i].IsPinned)
                    pinned.Add(i);
            }

            // Case 1: No pinned points -> scale all relative to start
            if (pinned.Count == 0)
            {
                SampleBezierCurve(_initSettings.ParticleCount * 4, out float currentLen);
                if (currentLen > 0.001f)
                {
                    float scale = targetLength / currentLen;
                    Vector3 startPos = ControlPoints[0].Position;
                    for (int i = 0; i < ControlPoints.Count; i++)
                    {
                        if (i > 0)
                            ControlPoints[i].Position = startPos + (ControlPoints[i].Position - startPos) * scale;
                        ControlPoints[i].TangentIn *= scale;
                        ControlPoints[i].TangentOut *= scale;
                    }
                }
                return;
            }

            // Case 2: Only 1 pinned point -> scale all relative to this pinned anchor
            if (pinned.Count == 1)
            {
                SampleBezierCurve(_initSettings.ParticleCount * 4, out float currentLen);
                if (currentLen > 0.001f)
                {
                    float scale = targetLength / currentLen;
                    int anchorIdx = pinned[0];
                    Vector3 anchorPos = ControlPoints[anchorIdx].Position;
                    for (int i = 0; i < ControlPoints.Count; i++)
                    {
                        if (i != anchorIdx)
                            ControlPoints[i].Position = anchorPos + (ControlPoints[i].Position - anchorPos) * scale;
                        ControlPoints[i].TangentIn *= scale;
                        ControlPoints[i].TangentOut *= scale;
                    }
                }
                return;
            }

            // Case 3: 2 or more pinned points -> pinned anchors stay in place!
            // If adjacent pinned points have no intermediate control point between them, insert one to allow sagging
            for (int p = 0; p < pinned.Count - 1; p++)
            {
                int pA = pinned[p];
                int pB = pinned[p + 1];
                float chordDist = Vector3.Distance(ControlPoints[pA].Position, ControlPoints[pB].Position);
                if (pB - pA == 1 && targetLength > chordDist)
                {
                    Vector3 midPos = (ControlPoints[pA].Position + ControlPoints[pB].Position) * 0.5f;
                    Vector3 tan = (ControlPoints[pB].Position - ControlPoints[pA].Position) * 0.25f;
                    var midPoint = new BezierPoint(midPos, -tan, tan)
                    {
                        IsPinned = false,
                        Mode = BezierControlMode.Mirrored
                    };
                    ControlPoints.Insert(pA + 1, midPoint);

                    // Shift subsequent pinned indices
                    for (int k = p + 1; k < pinned.Count; k++)
                    {
                        pinned[k]++;
                    }
                }
            }

            SampleBezierCurve(_initSettings.ParticleCount * 4, out float initialTotalLen);
            if (initialTotalLen < 0.001f) return;

            int firstPin = pinned[0];
            int lastPin = pinned[pinned.Count - 1];

            // Handle head tail (points before first pin, if any)
            if (firstPin > 0)
            {
                float headLen = MeasureBezierSpanLength(0, firstPin);
                float headRatio = headLen / initialTotalLen;
                float headTarget = targetLength * headRatio;
                float headScale = headLen > 0.001f ? headTarget / headLen : 1f;
                Vector3 anchor = ControlPoints[firstPin].Position;
                for (int i = 0; i < firstPin; i++)
                {
                    ControlPoints[i].Position = anchor + (ControlPoints[i].Position - anchor) * headScale;
                    ControlPoints[i].TangentIn *= headScale;
                    ControlPoints[i].TangentOut *= headScale;
                }
            }

            // Handle end tail (points after last pin, if any)
            if (lastPin < ControlPoints.Count - 1)
            {
                float tailLen = MeasureBezierSpanLength(lastPin, ControlPoints.Count - 1);
                float tailRatio = tailLen / initialTotalLen;
                float tailTarget = targetLength * tailRatio;
                float tailScale = tailLen > 0.001f ? tailTarget / tailLen : 1f;
                Vector3 anchor = ControlPoints[lastPin].Position;
                for (int i = lastPin + 1; i < ControlPoints.Count; i++)
                {
                    ControlPoints[i].Position = anchor + (ControlPoints[i].Position - anchor) * tailScale;
                    ControlPoints[i].TangentIn *= tailScale;
                    ControlPoints[i].TangentOut *= tailScale;
                }
            }

            // Expand each pinned span
            for (int p = 0; p < pinned.Count - 1; p++)
            {
                int pA = pinned[p];
                int pB = pinned[p + 1];

                Vector3 posA = ControlPoints[pA].Position;
                Vector3 posB = ControlPoints[pB].Position;
                Vector3 chord = posB - posA;
                float D = chord.magnitude;
                if (D < 0.001f)
                {
                    chord = Vector3.forward * 0.001f;
                    D = 0.001f;
                }
                Vector3 chordDir = chord / D;

                float currentSpanLen = MeasureBezierSpanLength(pA, pB);
                float spanTargetLen;
                if (pinned.Count == 2 && firstPin == 0 && lastPin == ControlPoints.Count - 1)
                {
                    spanTargetLen = targetLength;
                }
                else
                {
                    float ratio = currentSpanLen / initialTotalLen;
                    spanTargetLen = targetLength * ratio;
                }
                spanTargetLen = Mathf.Max(D, spanTargetLen);

                Vector3 sagDir = DetermineSagDirection(pA, pB, posA, chordDir);
                ExpandBezierSpan(pA, pB, posA, posB, chord, chordDir, D, sagDir, spanTargetLen);
            }
        }

        private Vector3 DetermineSagDirection(int pA, int pB, Vector3 posA, Vector3 chordDir)
        {
            Vector3 lateralSum = Vector3.zero;
            for (int k = pA + 1; k < pB; k++)
            {
                Vector3 toPt = ControlPoints[k].Position - posA;
                Vector3 onChord = posA + chordDir * Vector3.Dot(toPt, chordDir);
                lateralSum += (ControlPoints[k].Position - onChord);
            }

            Vector3 existingLateral = lateralSum - Vector3.Dot(lateralSum, chordDir) * chordDir;
            if (existingLateral.sqrMagnitude > 0.001f)
            {
                return existingLateral.normalized;
            }

            Vector3 down = Vector3.down;
            Vector3 proj = down - Vector3.Dot(down, chordDir) * chordDir;
            if (proj.sqrMagnitude > 0.001f)
            {
                return proj.normalized;
            }

            Vector3 fwd = Vector3.forward;
            proj = fwd - Vector3.Dot(fwd, chordDir) * chordDir;
            return (proj.sqrMagnitude > 0.001f) ? proj.normalized : Vector3.right;
        }

        private void ExpandBezierSpan(int pA, int pB, Vector3 posA, Vector3 posB, Vector3 chord, Vector3 chordDir, float D, Vector3 sagDir, float targetSpanLen)
        {
            int spanCount = pB - pA;
            if (targetSpanLen <= D + 0.0005f)
            {
                for (int k = pA + 1; k < pB; k++)
                {
                    float t = (float)(k - pA) / spanCount;
                    ControlPoints[k].Position = posA + chord * t;
                    Vector3 tVec = chordDir * (D / spanCount * 0.333f);
                    ControlPoints[k].TangentIn = -tVec;
                    ControlPoints[k].TangentOut = tVec;
                }
                Vector3 endTan = chordDir * (D / spanCount * 0.333f);
                ControlPoints[pA].TangentOut = endTan;
                ControlPoints[pB].TangentIn = -endTan;
                return;
            }

            float deltaL = Mathf.Max(0f, targetSpanLen - D);
            float h = Mathf.Sqrt(0.375f * D * deltaL);
            ApplySagToBezierSpan(pA, pB, posA, posB, chord, sagDir, h);

            float currentLen = MeasureBezierSpanLength(pA, pB);
            for (int iter = 0; iter < 5; iter++)
            {
                float error = targetSpanLen - currentLen;
                if (Mathf.Abs(error) < 0.002f) break;

                float dh = 0.01f;
                ApplySagToBezierSpan(pA, pB, posA, posB, chord, sagDir, h + dh);
                float testLen = MeasureBezierSpanLength(pA, pB);
                float dL_dh = (testLen - currentLen) / dh;

                if (Mathf.Abs(dL_dh) > 0.001f)
                {
                    h += error / dL_dh;
                    h = Mathf.Max(0f, h);
                }
                else
                {
                    h *= Mathf.Sqrt(Mathf.Max(0.01f, deltaL / Mathf.Max(0.001f, currentLen - D)));
                }

                ApplySagToBezierSpan(pA, pB, posA, posB, chord, sagDir, h);
                currentLen = MeasureBezierSpanLength(pA, pB);
            }
        }

        private void ApplySagToBezierSpan(int pA, int pB, Vector3 posA, Vector3 posB, Vector3 chord, Vector3 sagDir, float h)
        {
            int spanCount = pB - pA;
            for (int k = pA; k <= pB; k++)
            {
                float t = (float)(k - pA) / spanCount;
                if (k > pA && k < pB)
                {
                    float sagFactor = 4.0f * t * (1.0f - t);
                    ControlPoints[k].Position = posA + (chord * t) + sagDir * (sagFactor * h);
                }

                Vector3 slope = chord + sagDir * (4.0f * (1.0f - 2.0f * t) * h);
                Vector3 segTangent = slope * (1.0f / (3.0f * spanCount));

                if (k == pA)
                {
                    ControlPoints[k].TangentOut = segTangent;
                }
                else if (k == pB)
                {
                    ControlPoints[k].TangentIn = -segTangent;
                }
                else
                {
                    ControlPoints[k].TangentIn = -segTangent;
                    ControlPoints[k].TangentOut = segTangent;
                }
            }
        }

        private float MeasureBezierSpanLength(int pA, int pB)
        {
            float len = 0f;
            int resolution = 15;
            for (int seg = pA; seg < pB; seg++)
            {
                float3 prev = ControlPoints[seg].Position;
                for (int step = 1; step <= resolution; step++)
                {
                    float t = step / (float)resolution;
                    float3 p = EvaluateCubicBezier(seg, t);
                    len += math.distance(prev, p);
                    prev = p;
                }
            }
            return len;
        }

#if TENSIO_SPLINES_SUPPORT
        private void ScaleSplineCurve(float targetLength)
        {
            if (SplineReference == null || SplineReference.Spline == null || SplineReference.Spline.Count < 2) return;
            var spline = SplineReference.Spline;

            List<int> pinned = new List<int>();
            if (PinnedSplineKnots != null)
            {
                for (int i = 0; i < spline.Count; i++)
                {
                    if (PinnedSplineKnots.Contains(i))
                        pinned.Add(i);
                }
            }

            if (pinned.Count == 0)
            {
                float currentLen = spline.GetLength();
                if (currentLen > 0.001f)
                {
                    float scale = targetLength / currentLen;
                    float3 startPos = spline[0].Position;
                    for (int i = 0; i < spline.Count; i++)
                    {
                        var knot = spline[i];
                        if (i > 0)
                            knot.Position = startPos + (knot.Position - startPos) * scale;
                        knot.TangentIn *= scale;
                        knot.TangentOut *= scale;
                        spline[i] = knot;
                    }
                }
                return;
            }

            if (pinned.Count == 1)
            {
                float currentLen = spline.GetLength();
                if (currentLen > 0.001f)
                {
                    float scale = targetLength / currentLen;
                    int anchorIdx = pinned[0];
                    float3 anchorPos = spline[anchorIdx].Position;
                    for (int i = 0; i < spline.Count; i++)
                    {
                        var knot = spline[i];
                        if (i != anchorIdx)
                            knot.Position = anchorPos + (knot.Position - anchorPos) * scale;
                        knot.TangentIn *= scale;
                        knot.TangentOut *= scale;
                        spline[i] = knot;
                    }
                }
                return;
            }

            for (int p = 0; p < pinned.Count - 1; p++)
            {
                int pA = pinned[p];
                int pB = pinned[p + 1];
                float chordDist = math.distance(spline[pA].Position, spline[pB].Position);
                if (pB - pA == 1 && targetLength > chordDist)
                {
                    float3 midPos = (spline[pA].Position + spline[pB].Position) * 0.5f;
                    float3 tan = (spline[pB].Position - spline[pA].Position) * 0.25f;
                    spline.Insert(pA + 1, new BezierKnot(midPos, -tan, tan, quaternion.identity));

                    if (PinnedSplineKnots != null)
                    {
                        for (int k = 0; k < PinnedSplineKnots.Count; k++)
                        {
                            if (PinnedSplineKnots[k] >= pA + 1)
                                PinnedSplineKnots[k]++;
                        }
                    }

                    for (int k = p + 1; k < pinned.Count; k++)
                    {
                        pinned[k]++;
                    }
                }
            }

            float initialTotalLen = spline.GetLength();
            if (initialTotalLen < 0.001f) return;

            int firstPin = pinned[0];
            int lastPin = pinned[pinned.Count - 1];

            if (firstPin > 0)
            {
                float headLen = 0f;
                for (int k = 0; k < firstPin; k++) headLen += spline.GetCurveLength(k);
                float headScale = headLen > 0.001f ? (targetLength * (headLen / initialTotalLen)) / headLen : 1f;
                float3 anchor = spline[firstPin].Position;
                for (int i = 0; i < firstPin; i++)
                {
                    var knot = spline[i];
                    knot.Position = anchor + (knot.Position - anchor) * headScale;
                    knot.TangentIn *= headScale;
                    knot.TangentOut *= headScale;
                    spline[i] = knot;
                }
            }

            if (lastPin < spline.Count - 1)
            {
                float tailLen = 0f;
                for (int k = lastPin; k < spline.Count - 1; k++) tailLen += spline.GetCurveLength(k);
                float tailScale = tailLen > 0.001f ? (targetLength * (tailLen / initialTotalLen)) / tailLen : 1f;
                float3 anchor = spline[lastPin].Position;
                for (int i = lastPin + 1; i < spline.Count; i++)
                {
                    var knot = spline[i];
                    knot.Position = anchor + (knot.Position - anchor) * tailScale;
                    knot.TangentIn *= tailScale;
                    knot.TangentOut *= tailScale;
                    spline[i] = knot;
                }
            }

            for (int p = 0; p < pinned.Count - 1; p++)
            {
                int pA = pinned[p];
                int pB = pinned[p + 1];

                float3 posA = spline[pA].Position;
                float3 posB = spline[pB].Position;
                float3 chord = posB - posA;
                float D = math.length(chord);
                if (D < 0.001f)
                {
                    chord = new float3(0, 0, 0.001f);
                    D = 0.001f;
                }
                float3 chordDir = chord / D;

                float currentSpanLen = 0f;
                for (int k = pA; k < pB; k++) currentSpanLen += spline.GetCurveLength(k);

                float spanTargetLen;
                if (pinned.Count == 2 && firstPin == 0 && lastPin == spline.Count - 1)
                {
                    spanTargetLen = targetLength;
                }
                else
                {
                    float ratio = currentSpanLen / initialTotalLen;
                    spanTargetLen = targetLength * ratio;
                }
                spanTargetLen = Mathf.Max(D, spanTargetLen);

                float3 sagDir = DetermineSplineSagDirection(spline, pA, pB, posA, chordDir);
                ExpandSplineSpan(spline, pA, pB, posA, posB, chord, chordDir, D, sagDir, spanTargetLen);
            }
        }

        private float3 DetermineSplineSagDirection(Spline spline, int pA, int pB, float3 posA, float3 chordDir)
        {
            float3 lateralSum = float3.zero;
            for (int k = pA + 1; k < pB; k++)
            {
                float3 toPt = spline[k].Position - posA;
                float3 onChord = posA + chordDir * math.dot(toPt, chordDir);
                lateralSum += (spline[k].Position - onChord);
            }

            float3 existingLateral = lateralSum - chordDir * math.dot(lateralSum, chordDir);
            if (math.lengthsq(existingLateral) > 0.001f)
            {
                return math.normalize(existingLateral);
            }

            Vector3 localDown = SplineReference.transform.InverseTransformDirection(Vector3.down);
            Vector3 chordDirV = (Vector3)chordDir;
            Vector3 proj = localDown - Vector3.Dot(localDown, chordDirV) * chordDirV;
            if (proj.sqrMagnitude > 0.001f)
            {
                return (float3)proj.normalized;
            }

            Vector3 localFwd = SplineReference.transform.InverseTransformDirection(Vector3.forward);
            proj = localFwd - Vector3.Dot(localFwd, chordDirV) * chordDirV;
            return (proj.sqrMagnitude > 0.001f) ? (float3)proj.normalized : new float3(1, 0, 0);
        }

        private void ExpandSplineSpan(Spline spline, int pA, int pB, float3 posA, float3 posB, float3 chord, float3 chordDir, float D, float3 sagDir, float targetSpanLen)
        {
            int spanCount = pB - pA;
            if (targetSpanLen <= D + 0.0005f)
            {
                for (int k = pA + 1; k < pB; k++)
                {
                    float t = (float)(k - pA) / spanCount;
                    var knot = spline[k];
                    knot.Position = posA + chord * t;
                    float3 tVec = chordDir * (D / spanCount * 0.333f);
                    knot.TangentIn = -tVec;
                    knot.TangentOut = tVec;
                    knot.Rotation = quaternion.identity;
                    spline[k] = knot;
                }
                var knotA = spline[pA];
                knotA.TangentOut = chordDir * (D / spanCount * 0.333f);
                knotA.Rotation = quaternion.identity;
                spline[pA] = knotA;

                var knotB = spline[pB];
                knotB.TangentIn = -chordDir * (D / spanCount * 0.333f);
                knotB.Rotation = quaternion.identity;
                spline[pB] = knotB;
                return;
            }

            float deltaL = Mathf.Max(0f, targetSpanLen - D);
            float h = Mathf.Sqrt(0.375f * D * deltaL);
            ApplySagToSplineSpan(spline, pA, pB, posA, posB, chord, sagDir, h);

            float currentLen = MeasureSplineSpanLength(spline, pA, pB);
            for (int iter = 0; iter < 5; iter++)
            {
                float error = targetSpanLen - currentLen;
                if (Mathf.Abs(error) < 0.002f) break;

                float dh = 0.01f;
                ApplySagToSplineSpan(spline, pA, pB, posA, posB, chord, sagDir, h + dh);
                float testLen = MeasureSplineSpanLength(spline, pA, pB);
                float dL_dh = (testLen - currentLen) / dh;

                if (Mathf.Abs(dL_dh) > 0.001f)
                {
                    h += error / dL_dh;
                    h = Mathf.Max(0f, h);
                }
                else
                {
                    h *= Mathf.Sqrt(Mathf.Max(0.01f, deltaL / Mathf.Max(0.001f, currentLen - D)));
                }

                ApplySagToSplineSpan(spline, pA, pB, posA, posB, chord, sagDir, h);
                currentLen = MeasureSplineSpanLength(spline, pA, pB);
            }
        }

        private void ApplySagToSplineSpan(Spline spline, int pA, int pB, float3 posA, float3 posB, float3 chord, float3 sagDir, float h)
        {
            int spanCount = pB - pA;
            for (int k = pA; k <= pB; k++)
            {
                float t = (float)(k - pA) / spanCount;
                var knot = spline[k];
                knot.Rotation = quaternion.identity;

                if (k > pA && k < pB)
                {
                    float sagFactor = 4.0f * t * (1.0f - t);
                    knot.Position = posA + (chord * t) + sagDir * (sagFactor * h);
                }

                float3 slope = chord + sagDir * (4.0f * (1.0f - 2.0f * t) * h);
                float3 segTangent = slope * (1.0f / (3.0f * spanCount));

                if (k == pA)
                {
                    knot.TangentOut = segTangent;
                }
                else if (k == pB)
                {
                    knot.TangentIn = -segTangent;
                }
                else
                {
                    knot.TangentIn = -segTangent;
                    knot.TangentOut = segTangent;
                }
                spline[k] = knot;
            }
        }

        private float MeasureSplineSpanLength(Spline spline, int pA, int pB)
        {
            float len = 0f;
            for (int k = pA; k < pB; k++)
            {
                len += spline.GetCurveLength(k);
            }
            return len;
        }
#endif

        public void UpdateLengthFromEditor(float newLength)
        {
            _initSettings.Length = Mathf.Max(0.1f, newLength);

            if (Application.isPlaying)
            {
                if (_solver != null) _solver.SetRopeLength(_initSettings.Length);
            }
            else
            {
                if (InitMode == RopeInitMode.Bezier && UseBezierCurve && ControlPoints.Count >= 2)
                {
                    RefreshParticlesFromBezier();
                }
#if TENSIO_SPLINES_SUPPORT
                else if (InitMode == RopeInitMode.Spline)
                {
                    RefreshParticlesFromSpline();
                }
#endif
                else
                {
                    RefreshParticlesFromPins();
                }
            }
        }

        #endregion

        #region Internal Logic

        private void InitializeFromSplit(int count, float newLength, float3[] pos, float3[] prev, float3[] vel, float[] invMass,
            List<PinConstraint> pins)
        {
            _initSettings.ParticleCount = count;
            _initSettings.Length = newLength;

            _pinConstraints = pins;
            _initSettings.PinFirstParticle = false;
            _physicsSettings.BreakIndexOverride = -1;

            _solver?.Dispose();
            _solver = new RopeSolver(count, newLength, pos[0], RopeId);
            _enableBreakTime = Time.time + _physicsSettings.BreakWarmup;

            _solver.SetState(pos, prev, vel, invMass);

            if (_initSettings.Type == RopeInitSettings.RopeType.Chain) GenerateChainTethers();

            PreparePinDataAndHandleCoupling();
            _solver.UpdatePinConstraints(_pinBuffer);
        }

        private void WarmupRope()
        {
            // Run steps to settle the rope dynamically based on gravity before the first rendered frame.
            
            NativeArray<SphereColliderData> sphereNative = default;
            NativeArray<PlaneColliderData> planeNative = default;
            NativeArray<CapsuleColliderData> capsuleNative = default;
            NativeArray<BoxColliderData> boxNative = default;

            RopeCollisionManager.GetSphereData(ref sphereNative, Allocator.TempJob);
            RopeCollisionManager.GetPlaneData(ref planeNative, Allocator.TempJob);
            RopeCollisionManager.GetCapsuleData(ref capsuleNative, Allocator.TempJob);
            RopeCollisionManager.GetBoxData(ref boxNative, Allocator.TempJob);

            PreparePinDataAndHandleCoupling();
            _solver.UpdateColliders(sphereNative, planeNative, capsuleNative, boxNative);
            
            if (sphereNative.IsCreated) sphereNative.Dispose();
            if (planeNative.IsCreated) planeNative.Dispose();
            if (capsuleNative.IsCreated) capsuleNative.Dispose();
            if (boxNative.IsCreated) boxNative.Dispose();

            _solver.UpdatePinConstraints(_pinBuffer);

            int warmupSteps = _physicsSettings.WarmupSteps;
            if (warmupSteps <= 0) return;

            float dt = 0.016f; // Standard frame
            
            // Use configured gravity but with high drag so it settles quickly without oscillating endlessly
            for (int i = 0; i < warmupSteps; i++)
            {
                _solver.Step(dt, _physicsSettings.SubSteps, _physicsSettings.Gravity, 0f, 0.2f, _physicsSettings.ParticleRadius, 0.1f, 0.1f,
                    float3.zero, 0, 0, 5.0f, // High AirDrag (5.0) effectively damps velocity
                    0, default, 0, 0.5f, _physicsSettings.CollisionMask, _initSettings.IsClosedLoop); 
            }
            _solver.CompleteJobs();
            _solver.Positions.CopyTo(_solver.PrevPositions);
        }

        private void ProcessStatsAndEvents()
        {
            float actualLen = _solver.TotalLength;
            CurrentStressRatio = (_initSettings.Length > 0) ? actualLen / _initSettings.Length : 1.0f;
            AverageVelocity = (_initSettings.ParticleCount > 0)
                ? _solver.TotalVelocity / _initSettings.ParticleCount
                : 0f;

            var events = _solver.CollisionEvents;
            while (events.TryDequeue(out var ev))
            {
                OnCollisionImpulse?.Invoke(ev.Position, ev.VelocityMagnitude);
            }

            var proxyEvents = _solver.ProxyEvents;
            if (_physicsSettings.EnableTwoWayCoupling)
            {
                while (proxyEvents.TryDequeue(out var pev))
                {
                    RopeCollisionManager.ApplyProxyImpulse(pev.ProxyType, pev.ProxyIndex, pev.Impulse, pev.Point, _physicsSettings.TwoWayCouplingForce);
                }
            }
            else
            {
                proxyEvents.Clear();
            }
        }

        private void PreparePinDataAndHandleCoupling()
        {
            // Ensure solver jobs are finished before modifying mass
            if (_solver != null) _solver.CompleteJobs();

            int activePinCount = 0;
            for (int i = 0; i < _pinConstraints.Count; i++)
            {
                if (!_pinConstraints[i].UseKinematicCoupling) activePinCount++;
            }

            if (_pinBuffer == null || _pinBuffer.Length != activePinCount)
                _pinBuffer = new PinConstraintData[activePinCount];

            int bufferIdx = 0;
            NativeArray<float> invMasses = _solver.InverseMasses;
            
            // Reset all masses to default before applying pin overrides.
            // This handles cases where particles are unpinned dynamically.
            float defaultInvMass = 1.0f / Mathf.Max(0.001f, _physicsSettings.ParticleMass);
            for (int i = 0; i < invMasses.Length; i++) invMasses[i] = defaultInvMass;

            float thresholdSq = _physicsSettings.TeleportThreshold * _physicsSettings.TeleportThreshold;

            for (int i = 0; i < _pinConstraints.Count; i++)
            {
                var pin = _pinConstraints[i];
                if (pin.Target == null) continue;

                if (pin.CachedBody == null) pin.CachedBody = pin.Target.GetComponent<Rigidbody>();
                Rigidbody rb = pin.CachedBody;

                float3 targetWorldPos = pin.Target.TransformPoint(pin.LocalOffset);
                float3 currentPos = _solver.Positions[pin.ParticleIndex];

                // TELEPORT CHECK: If the target moved too fast, reset velocity by syncing PrevPosition
                if (math.distancesq(targetWorldPos, currentPos) > thresholdSq)
                {
                    _solver.SetParticlePosition(pin.ParticleIndex, targetWorldPos);
                }

                if (pin.UseKinematicCoupling && rb != null)
                {
                    if (!rb.isKinematic)
                    {
                        pin.WasKinematic = rb.isKinematic;
                        rb.isKinematic = true;
                    }

                    float injectedInvMass = (rb.mass > 0) ? 1.0f / rb.mass : 0f;
                    invMasses[pin.ParticleIndex] = injectedInvMass;
                    continue;
                }

                // If it's a static pin (no dynamic rigidbody), we set inverse mass to 0
                // so the solver treats it as kinematic/infinite mass for perfect stability.
                bool isDynamicPin = rb != null && !rb.isKinematic;
                invMasses[pin.ParticleIndex] = isDynamicPin ? defaultInvMass : 0f;

                float invMass = 0f;
                float3x3 invInertia = float3x3.zero;
                float3 bodyPos = float3.zero;

                if (isDynamicPin)
                {
                    invMass = (rb.mass > 0) ? 1.0f / rb.mass : 0f;
                    float3 invTensor = rb.inertiaTensor;
                    invTensor = new float3(invTensor.x > 0 ? 1f / invTensor.x : 0,
                        invTensor.y > 0 ? 1f / invTensor.y : 0, invTensor.z > 0 ? 1f / invTensor.z : 0);
                    float3x3 R = new float3x3(rb.rotation);
                    float3x3 I_inv_local = new float3x3(invTensor.x, 0, 0, 0, invTensor.y, 0, 0, 0, invTensor.z);
                    invInertia = math.mul(math.mul(R, I_inv_local), math.transpose(R));
                    bodyPos = rb.worldCenterOfMass;
                }

                _pinBuffer[bufferIdx] = new PinConstraintData
                {
                    ParticleIndex = pin.ParticleIndex,
                    StartPosition = currentPos,
                    TargetPosition = targetWorldPos,
                    BodyInverseMass = invMass,
                    BodyInverseInertia = invInertia,
                    BodyPosition = bodyPos,
                    Compliance = 0.0f 
                };
                bufferIdx++;
                }
                }
        
        private void ApplyKinematicTransforms()
        {
            NativeArray<float3> positions = _solver.Positions;
            for (int i = 0; i < _pinConstraints.Count; i++)
            {
                var pin = _pinConstraints[i];
                if (pin.UseKinematicCoupling && pin.CachedBody != null)
                {
                    float3 particlePos = positions[pin.ParticleIndex];
                    Vector3 targetWorldPos = (Vector3)particlePos - pin.Target.TransformVector(pin.LocalOffset);
                    pin.CachedBody.MovePosition(targetWorldPos);
                }
            }
        }

        private void ApplyPinForces()
        {
            if (_solver == null || !_solver.IsInitialized) return;
            NativeArray<float3> impulses = _solver.PinImpulses;

            for (int i = 0; i < _pinConstraints.Count; i++)
            {
                var pin = _pinConstraints[i];
                if (pin.CachedBody != null && !pin.CachedBody.isKinematic)
                {
                    float3 impulse = -impulses[i];
                    pin.CachedBody.AddForceAtPosition(impulse, pin.Target.TransformPoint(pin.LocalOffset),
                        ForceMode.Impulse);
                }
            }
        }

        private void PerformExactSplitLogic(float3[] posA, float3[] prevA, float3[] velA, float[] invMassA, float lengthA,
            float3[] posB, float3[] prevB, float3[] velB, float[] invMassB, float lengthB, int segmentIndex)
        {
            var pinsA = new List<PinConstraint>();
            var pinsB = new List<PinConstraint>();
            foreach (var pin in _pinConstraints)
            {
                if (pin.ParticleIndex <= segmentIndex)
                {
                    pinsA.Add(pin);
                }
                else // pin.ParticleIndex >= segmentIndex + 1
                {
                    pinsB.Add(new PinConstraint
                    {
                        ParticleIndex = pin.ParticleIndex - segmentIndex,
                        Target = pin.Target,
                        LocalOffset = pin.LocalOffset,
                        CachedBody = pin.CachedBody,
                        UseKinematicCoupling = pin.UseKinematicCoupling
                    });
                }
            }

            int countA = posA.Length;
            int countB = posB.Length;

            if (countB >= 2)
            {
                GameObject partB = Instantiate(this.gameObject, transform.parent);
                partB.name = name + "_Split";
                partB.GetComponent<RopeController>().InitializeFromSplit(countB, lengthB, posB, prevB, velB, invMassB, pinsB);

                if (_physicsSettings.UnpinnedDebrisMode != RopeInitSettings.DebrisMode.None && pinsB.Count == 0)
                {
                    var debris = partB.AddComponent<RopeDebris>();
                    debris.Initialize(_physicsSettings.DebrisLifetime, _physicsSettings.DebrisFadeTime, _physicsSettings.UnpinnedDebrisMode);
                }
            }

            if (countA >= 2)
            {
                this.InitializeFromSplit(countA, lengthA, posA, prevA, velA, invMassA, pinsA);

                if (_physicsSettings.UnpinnedDebrisMode != RopeInitSettings.DebrisMode.None && pinsA.Count == 0)
                {
                    var debris = this.gameObject.AddComponent<RopeDebris>();
                    debris.Initialize(_physicsSettings.DebrisLifetime, _physicsSettings.DebrisFadeTime, _physicsSettings.UnpinnedDebrisMode);
                }
            }
            else
            {
                Destroy(gameObject);
            }

            OnRopeCut?.Invoke();
        }

        private void GenerateChainTethers()
        {
            List<int> anchors = new List<int>();
            foreach (var p in _pinConstraints) anchors.Add(p.ParticleIndex);
            if (anchors.Count == 0) return;

            List<TetherConstraintData> tethers = new List<TetherConstraintData>();
            float segLen = _initSettings.Length / (_initSettings.ParticleCount - 1);
            int resolution = 3;

            for (int i = 0; i < _initSettings.ParticleCount; i++)
            {
                if (i % resolution != 0 && i != _initSettings.ParticleCount - 1) continue;

                int bestAnchor = -1;
                int minDiff = int.MaxValue;
                foreach (var a in anchors)
                {
                    int diff = Mathf.Abs(i - a);
                    if (diff > 0 && diff < minDiff)
                    {
                        minDiff = diff;
                        bestAnchor = a;
                    }
                }

                if (bestAnchor != -1)
                {
                    tethers.Add(new TetherConstraintData
                    {
                        ParticleIndex = i,
                        AnchorParticleIndex = bestAnchor,
                        MaxLength = minDiff * segLen * 1.05f
                    });
                }
            }

            _solver.UpdateTethers(tethers.ToArray());
        }

#if TENSIO_SPLINES_SUPPORT
        private float3[] SampleSpline(int count, out float totalLength)
        {
            totalLength = 0f;
            if (count < 2) return new float3[count];

            float defaultLen = (_initSettings != null && _initSettings.Length > 0.01f) ? _initSettings.Length : 5.0f;
            float3 startPos = (float3)transform.position;

            if (SplineReference == null || SplineReference.Spline == null || SplineReference.Spline.Count == 0)
            {
                totalLength = defaultLen;
                float3[] fallback = new float3[count];
                float step = defaultLen / (count - 1);
                for (int i = 0; i < count; i++)
                {
                    fallback[i] = startPos + new float3(0, -1, 0) * (i * step);
                }
                return fallback;
            }

            float splineLen = SplineReference.Spline.GetLength();
            var localToWorld = SplineReference.transform.localToWorldMatrix;

            if (splineLen <= 0.001f)
            {
                totalLength = defaultLen;
                float3[] fallback = new float3[count];
                float3 knot0 = localToWorld.MultiplyPoint3x4(SplineReference.Spline[0].Position);
                float step = defaultLen / (count - 1);
                for (int i = 0; i < count; i++)
                {
                    fallback[i] = knot0 + new float3(0, -0.01f, 0) * (i * step);
                }
                return fallback;
            }

            totalLength = splineLen;
            float3[] result = new float3[count];
            float spacing = splineLen / (count - 1);

            for (int i = 0; i < count; i++)
            {
                float t = math.clamp((i * spacing) / splineLen, 0f, 1f);
                float3 localPos = SplineUtility.EvaluatePosition(SplineReference.Spline, t);
                float3 worldPos = localToWorld.MultiplyPoint3x4(localPos);
                if (!math.all(math.isfinite(worldPos)))
                {
                    worldPos = (i > 0) ? result[i - 1] + new float3(0, -0.01f, 0) : startPos;
                }
                result[i] = worldPos;
            }
            return result;
        }
#endif

        private float3[] SampleBezierCurve(int count, out float totalLength)
        {
            totalLength = 0f;
            if (count < 2) return new float3[count];

            float3[] result = new float3[count];
            
            int segments = ControlPoints.Count - 1;
            int resolution = 30; 
            
            List<float> segmentLengths = new List<float>();
            List<float> accumulatedLengths = new List<float>();
            accumulatedLengths.Add(0f);

            for (int i = 0; i < segments; i++)
            {
                float len = GetSegmentLength(i, resolution);
                segmentLengths.Add(len);
                totalLength += len;
                accumulatedLengths.Add(totalLength);
            }
            
            // Loop Length
            if (InitMode == RopeInitMode.Bezier && InitSettings.IsClosedLoop)
            {
            }

            float spacing = totalLength / (count - 1);
            result[0] = ControlPoints[0].Position;
            
            for (int i = 1; i < count - 1; i++)
            {
                float targetDist = i * spacing;
                result[i] = GetPointAtDistance(targetDist, accumulatedLengths, segmentLengths);
            }
            
            result[count - 1] = ControlPoints[segments].Position;

            return result;
        }

        private float GetSegmentLength(int segIndex, int resolution)
        {
            float len = 0f;
            float3 prev = ControlPoints[segIndex].Position;
            for (int i = 1; i <= resolution; i++)
            {
                float t = i / (float)resolution;
                float3 p = EvaluateCubicBezier(segIndex, t);
                len += math.distance(prev, p);
                prev = p;
            }
            return len;
        }

        private float3 GetPointAtDistance(float dist, List<float> accumulated, List<float> segLengths)
        {
            for (int i = 0; i < accumulated.Count - 1; i++)
            {
                if (dist <= accumulated[i + 1])
                {
                    float distInSeg = dist - accumulated[i];
                    float t = EstimateT(i, distInSeg, segLengths[i]);
                    return EvaluateCubicBezier(i, t);
                }
            }
            return ControlPoints[ControlPoints.Count - 1].Position;
        }

        private float EstimateT(int segIndex, float dist, float totalSegLen)
        {
            if (totalSegLen < 0.0001f) return 0f;
            float t = math.clamp(dist / totalSegLen, 0f, 1f); 
            
            float3 start = ControlPoints[segIndex].Position;
            float currentDist = 0;
            float3 prev = start;
            int steps = 20;
            for(int k=1; k<=steps; k++)
            {
                float testT = k / (float)steps;
                float3 pt = EvaluateCubicBezier(segIndex, testT);
                float d = math.distance(prev, pt);
                if (currentDist + d >= dist)
                {
                    float remaining = dist - currentDist;
                    float fraction = d > 1e-6f ? (remaining / d) * (1f / steps) : 0f;
                    return math.clamp((k - 1) / (float)steps + fraction, 0f, 1f);
                }
                currentDist += d;
                prev = pt;
            }
            return t;
        }

        private float3 EvaluateCubicBezier(int segIndex, float t)
        {
            BezierPoint p0 = ControlPoints[segIndex];
            BezierPoint p3 = ControlPoints[segIndex + 1];
            
            float3 p1 = (float3)p0.Position + (float3)p0.TangentOut;
            float3 p2 = (float3)p3.Position + (float3)p3.TangentIn; 
            
            return CalculateCubic(t, p0.Position, p1, p2, p3.Position);
        }

        private float3 CalculateCubic(float t, float3 p0, float3 p1, float3 p2, float3 p3)
        {
            float u = 1 - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            return uuu * p0 + 3 * uu * t * p1 + 3 * u * tt * p2 + ttt * p3;
        }

        #endregion

        #region Jobs

        [BurstCompile]
        struct RopeRaycastJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            public float3 Origin;
            public float3 Direction;
            public float Radius;
            [WriteOnly] public NativeArray<float> Results;
            [WriteOnly] public NativeArray<float> SegmentFractions;

            public void Execute(int i)
            {
                float3 p0 = Positions[i];
                float3 p1 = Positions[i + 1];
                float3 d1 = p1 - p0;
                float3 d2 = Direction;
                float3 r = p0 - Origin;
                float a = math.dot(d1, d1);
                float b = math.dot(d1, d2);
                float c = math.dot(d2, d2);
                float d = math.dot(d1, r);
                float e = math.dot(d2, r);
                float det = a * c - b * b;
                float s = (det < 1e-6f) ? 0f : (b * e - c * d) / det;
                s = math.clamp(s, 0f, 1f);
                float t = math.dot(p0 + d1 * s - Origin, d2);

                if (t < 0)
                {
                    Results[i] = -1;
                    SegmentFractions[i] = 0;
                    return;
                }

                float3 pRay = Origin + d2 * t;
                float3 pSeg = p0 + d1 * s;
                if (math.lengthsq(pSeg - pRay) <= Radius * Radius)
                {
                     Results[i] = t;
                     SegmentFractions[i] = s;
                }
                else
                {
                     Results[i] = -1;
                     SegmentFractions[i] = 0;
                }
            }
        }

        
        #endregion
    }
}