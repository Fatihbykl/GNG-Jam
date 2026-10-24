using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

namespace Tensio
{
    #region Data Structs

    /// <summary>
    /// Represents a Sphere Collider for Burst jobs.
    /// </summary>
    [System.Serializable]
    public struct SphereColliderData
    {
        /// <summary>
        /// World space center position of the sphere.
        /// </summary>
        [Tooltip("World space center position of the sphere.")]
        public float3 Position;

        /// <summary>
        /// Radius of the sphere.
        /// </summary>
        [Tooltip("Radius of the sphere.")]
        public float Radius;
    }

    /// <summary>
    /// Represents a Plane Collider for Burst jobs.
    /// </summary>
    [System.Serializable]
    public struct PlaneColliderData
    {
        /// <summary>
        /// The normal vector of the plane (must be normalized).
        /// </summary>
        [Tooltip("The normal vector of the plane (must be normalized).")]
        public float3 Normal;

        /// <summary>
        /// A point lying on the plane.
        /// </summary>
        [Tooltip("A point lying on the plane.")]
        public float3 PointOnPlane;
    }

    /// <summary>
    /// Represents a Box Collider for Burst jobs.
    /// </summary>
    [System.Serializable]
    public struct BoxColliderData
    {
        /// <summary>
        /// World space center of the box.
        /// </summary>
        [Tooltip("World space center of the box.")]
        public float3 Center;

        /// <summary>
        /// The size of the box in local space.
        /// </summary>
        [Tooltip("The size of the box in local space.")]
        public float3 Size;

        /// <summary>
        /// The rotation of the box.
        /// </summary>
        [Tooltip("The rotation of the box.")]
        public Quaternion Rotation;
    }

    /// <summary>
    /// Represents a Capsule Collider for Burst jobs.
    /// </summary>
    [System.Serializable]
    public struct CapsuleColliderData
    {
        /// <summary>
        /// World space start position of the capsule segment.
        /// </summary>
        [Tooltip("World space start position of the capsule segment.")]
        public float3 Start;

        /// <summary>
        /// World space end position of the capsule segment.
        /// </summary>
        [Tooltip("World space end position of the capsule segment.")]
        public float3 End;

        /// <summary>
        /// Radius of the capsule.
        /// </summary>
        [Tooltip("Radius of the capsule.")]
        public float Radius;
    }

    /// <summary>
    /// Represents an event where a proxy collider is hit by a rope particle.
    /// Used for two-way coupling.
    /// </summary>
    public struct ProxyCollisionEvent
    {
        public int ProxyType; // 0=Sphere, 1=Capsule, 2=Box, 3=Plane
        public int ProxyIndex;
        public float3 Impulse;
        public float3 Point;
    }

    /// <summary>
    /// Defines a constraint pinning a particle to a world position or Rigidbody.
    /// </summary>
    public struct PinConstraintData
    {
        /// <summary>
        /// The index of the particle to pin.
        /// </summary>
        [Tooltip("The index of the particle to pin.")]
        public int ParticleIndex;

        /// <summary>
        /// The starting position at the beginning of the frame.
        /// </summary>
        public float3 StartPosition;

        /// <summary>
        /// The target position in world space at the end of the frame.
        /// </summary>
        [Tooltip("The target position in world space.")]
        public float3 TargetPosition;

        /// <summary>
        /// The compliance (softness) of the constraint. 0 is rigid.
        /// </summary>
        [Tooltip("The compliance (softness) of the constraint. 0 is rigid.")]
        public float Compliance;

        // Dynamic Body Data

        /// <summary>
        /// Inverse mass of the attached body (0 if static).
        /// </summary>
        [Tooltip("Inverse mass of the attached body (0 if static).")]
        public float BodyInverseMass;

        /// <summary>
        /// Inverse inertia tensor of the attached body.
        /// </summary>
        [Tooltip("Inverse inertia tensor of the attached body.")]
        public float3x3 BodyInverseInertia;

        /// <summary>
        /// World position of the attached body.
        /// </summary>
        [Tooltip("World position of the attached body.")]
        public float3 BodyPosition;
    }

    /// <summary>
    /// Long-range constraint for chains to prevent excessive stretching.
    /// </summary>
    public struct TetherConstraintData
    {
        /// <summary>
        /// The index of the particle being tethered.
        /// </summary>
        [Tooltip("The index of the particle being tethered.")]
        public int ParticleIndex;

        /// <summary>
        /// The index of the anchor particle.
        /// </summary>
        [Tooltip("The index of the anchor particle.")]
        public int AnchorParticleIndex;

        /// <summary>
        /// The maximum allowed distance between the particle and the anchor.
        /// </summary>
        [Tooltip("The maximum allowed distance between the particle and the anchor.")]
        public float MaxLength;
    }

    /// <summary>
    /// Records a collision event during the simulation step.
    /// </summary>
    public struct CollisionEvent
    {
        /// <summary>
        /// World position where the collision occurred.
        /// </summary>
        [Tooltip("World position where the collision occurred.")]
        public float3 Position;

        /// <summary>
        /// Magnitude of the velocity at the time of impact.
        /// </summary>
        [Tooltip("Magnitude of the velocity at the time of impact.")]
        public float VelocityMagnitude;
    }

    #endregion

    /// <summary>
    /// High-performance XPBD Physics Solver.
    /// Manages NativeArrays and schedules Burst Jobs.
    /// </summary>
    public class RopeSolver
    {
        // Physics State
        private NativeArray<float3> _positions;
        private NativeArray<float3> _prevPositions;
        private NativeArray<float3> _lastFramePositions;
        private NativeArray<float3> _velocities;
        private NativeArray<float> _inverseMasses;

        // Colliders (Standard)
        private NativeArray<SphereColliderData> _sphereColliders;
        private NativeArray<PlaneColliderData> _planeColliders;
        private NativeArray<BoxColliderData> _boxColliders;
        private NativeArray<CapsuleColliderData> _capsuleColliders;

        // Global Mesh Collision (SphereCast)
        private NativeArray<SpherecastCommand> _sphereCommands;
        private NativeArray<RaycastHit> _rayHits;

        // Constraints
        private NativeArray<PinConstraintData> _pinConstraints;
        private NativeArray<float3> _pinImpulses;
        private NativeArray<TetherConstraintData> _tetherConstraints;

        // Internal Systems
        private NativeParallelMultiHashMap<int, SpatialEntry> _spatialMap;
        private NativeReference<int> _breakIndex;
        private NativeQueue<CollisionEvent> _collisionEvents;
        private NativeQueue<ProxyCollisionEvent> _proxyEvents;
        private NativeReference<float2> _ropeStats; // x = Length, y = Velocity

        private int _particleCount;
        private float _segmentLength;
        private JobHandle _jobHandle;

        /// <summary>
        /// Returns true if the solver's internal buffers are allocated.
        /// </summary>
        public bool IsInitialized => _positions.IsCreated;

        // Public Read-Only Access
        /// <summary>
        /// Current positions of all particles.
        /// </summary>
        public NativeArray<float3> Positions => _positions;

        /// <summary>
        /// Positions of all particles in the previous frame.
        /// </summary>
        public NativeArray<float3> PrevPositions => _prevPositions;
        
        public NativeArray<float3> LastFramePositions => _lastFramePositions;

        /// <summary>
        /// Current velocities of all particles.
        /// </summary>
        public NativeArray<float3> Velocities => _velocities;

        /// <summary>
        /// Inverse masses of all particles (0 = static, >0 = dynamic).
        /// </summary>
        public NativeArray<float> InverseMasses => _inverseMasses;

        /// <summary>
        /// Accumulated impulses for pin constraints.
        /// </summary>
        public NativeArray<float3> PinImpulses => _pinImpulses;

        /// <summary>
        /// Index of the particle where a break occurred, or -1 if none.
        /// </summary>
        public int BreakIndex => _breakIndex.Value;

        /// <summary>
        /// Queue of collision events detected in the last step.
        /// </summary>
        public NativeQueue<CollisionEvent> CollisionEvents => _collisionEvents;

        /// <summary>
        /// Queue of proxy collision events detected in the last step.
        /// </summary>
        public NativeQueue<ProxyCollisionEvent> ProxyEvents => _proxyEvents;

        /// <summary>
        /// Total length of the rope.
        /// </summary>
        public float TotalLength => _ropeStats.Value.x;

        /// <summary>
        /// Total velocity magnitude of the rope.
        /// </summary>
        public float TotalVelocity => _ropeStats.Value.y;

        /// <summary>
        /// Returns the current JobHandle of the solver.
        /// </summary>
        public JobHandle GetJobHandle() => _jobHandle;

        /// <summary>
        /// Unique identifier for this rope instance.
        /// </summary>
        public int RopeId { get; private set; }

        /// <summary>
        /// Spatial hashing entry for self-collision detection.
        /// </summary>
        public struct SpatialEntry
        {
            /// <summary>
            /// Index of the particle.
            /// </summary>
            [Tooltip("Index of the particle.")]
            public int Index;

            /// <summary>
            /// True if this is a virtual particle (midpoint).
            /// </summary>
            [Tooltip("True if this is a virtual particle (midpoint).")]
            public bool IsVirtual;
        }

        /// <summary>
        /// Spatial hashing entry for inter-rope collision detection.
        /// </summary>
        public struct GlobalSpatialEntry
        {
            /// <summary>
            /// ID of the rope this segment belongs to.
            /// </summary>
            [Tooltip("ID of the rope this segment belongs to.")]
            public int RopeId;

            /// <summary>
            /// Start position of the segment.
            /// </summary>
            [Tooltip("Start position of the segment.")]
            public float3 Position;

            /// <summary>
            /// End position of the segment.
            /// </summary>
            [Tooltip("End position of the segment.")]
            public float3 EndPosition;

            /// <summary>
            /// Start position of the segment from the previous frame.
            /// </summary>
            [Tooltip("Start position of the segment from the previous frame.")]
            public float3 PrevPosition;

            /// <summary>
            /// End position of the segment from the previous frame.
            /// </summary>
            [Tooltip("End position of the segment from the previous frame.")]
            public float3 PrevEndPosition;

            /// <summary>
            /// Radius of the rope segment.
            /// </summary>
            [Tooltip("Radius of the rope segment.")]
            public float Radius;

            /// <summary>
            /// Inverse mass at the start of the segment.
            /// </summary>
            public float InverseMass0;

            /// <summary>
            /// Inverse mass at the end of the segment.
            /// </summary>
            public float InverseMass1;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RopeSolver"/> class.
        /// </summary>
        /// <param name="particleCount">Number of particles in the rope.</param>
        /// <param name="ropeLength">Total length of the rope.</param>
        /// <param name="startPosition">Starting world position.</param>
        /// <param name="ropeId">Unique ID for this rope.</param>
        public RopeSolver(int particleCount, float ropeLength, float3 startPosition, int ropeId = 0)
        {
            _particleCount = particleCount;
            _segmentLength = ropeLength / (particleCount - 1);
            RopeId = ropeId;
            AllocateMemory();
            InitializeRope(startPosition);
        }

        private void AllocateMemory()
        {
            _positions = new NativeArray<float3>(_particleCount, Allocator.Persistent);
            _prevPositions = new NativeArray<float3>(_particleCount, Allocator.Persistent);
            _lastFramePositions = new NativeArray<float3>(_particleCount, Allocator.Persistent);
            _velocities = new NativeArray<float3>(_particleCount, Allocator.Persistent);
            _inverseMasses = new NativeArray<float>(_particleCount, Allocator.Persistent);

            _spatialMap = new NativeParallelMultiHashMap<int, SpatialEntry>(_particleCount * 6, Allocator.Persistent);
            _breakIndex = new NativeReference<int>(-1, Allocator.Persistent);
            _collisionEvents = new NativeQueue<CollisionEvent>(Allocator.Persistent);
            _proxyEvents = new NativeQueue<ProxyCollisionEvent>(Allocator.Persistent);
            _ropeStats = new NativeReference<float2>(float2.zero, Allocator.Persistent);

            // Global Mesh Collision Buffers (Double size for Particle + Segment casts)
            _sphereCommands = new NativeArray<SpherecastCommand>(_particleCount * 2, Allocator.Persistent);
            _rayHits = new NativeArray<RaycastHit>(_particleCount * 2, Allocator.Persistent);
        }

        private void InitializeRope(float3 startPosition)
        {
            float3 direction = new float3(0, -1, 0);
            for (int i = 0; i < _particleCount; i++)
            {
                float3 pos = startPosition + direction * (i * _segmentLength);
                _positions[i] = pos;
                _prevPositions[i] = pos;
                _lastFramePositions[i] = pos;
                _velocities[i] = float3.zero;
                _inverseMasses[i] = 1.0f;
            }

            _breakIndex.Value = -1;
        }

        /// <summary>
        /// Manually sets the full state of the rope from NativeArrays.
        /// </summary>
        public void SetInitialData(NativeArray<float3> pos, NativeArray<float3> vel, NativeArray<float> invMass)
        {
            if (pos.Length != _particleCount) return;

            NativeArray<float3>.Copy(pos, _positions, _particleCount);
            NativeArray<float3>.Copy(vel, _velocities, _particleCount);
            NativeArray<float>.Copy(invMass, _inverseMasses, _particleCount);
            NativeArray<float3>.Copy(pos, _prevPositions, _particleCount);
        }

        /// <summary>
        /// Sets the inverse mass for a specific particle.
        /// </summary>
        public void SetInverseMass(int index, float invMass)
        {
            _inverseMasses[index] = invMass;
        }

        /// <summary>
        /// Manually sets the full state of the rope from managed arrays.
        /// </summary>
        public void SetState(float3[] pos, float3[] prevPos, float3[] vel, float[] invMass)
        {
            if (pos.Length != _particleCount) return;
            _positions.CopyFrom(pos);
            _prevPositions.CopyFrom(prevPos);
            _lastFramePositions.CopyFrom(pos);
            _velocities.CopyFrom(vel);
            _inverseMasses.CopyFrom(invMass);
        }

        /// <summary>
        /// Updates the target rope length and recalculates segment length.
        /// </summary>
        public void SetRopeLength(float newLength)
        {
            if (_particleCount < 2) return;
            _segmentLength = newLength / (_particleCount - 1);
        }

        /// <summary>
        /// Disposes all native collections and completes running jobs.
        /// </summary>
        public void Dispose()
        {
            _jobHandle.Complete();

            if (_positions.IsCreated) _positions.Dispose();
            if (_prevPositions.IsCreated) _prevPositions.Dispose();
            if (_lastFramePositions.IsCreated) _lastFramePositions.Dispose();
            if (_velocities.IsCreated) _velocities.Dispose();
            if (_inverseMasses.IsCreated) _inverseMasses.Dispose();

            if (_sphereColliders.IsCreated) _sphereColliders.Dispose();
            if (_planeColliders.IsCreated) _planeColliders.Dispose();
            if (_capsuleColliders.IsCreated) _capsuleColliders.Dispose();
            if (_boxColliders.IsCreated) _boxColliders.Dispose();

            if (_pinConstraints.IsCreated) _pinConstraints.Dispose();
            if (_pinImpulses.IsCreated) _pinImpulses.Dispose();
            if (_tetherConstraints.IsCreated) _tetherConstraints.Dispose();

            if (_spatialMap.IsCreated) _spatialMap.Dispose();
            if (_breakIndex.IsCreated) _breakIndex.Dispose();
            if (_collisionEvents.IsCreated) _collisionEvents.Dispose();
            if (_proxyEvents.IsCreated) _proxyEvents.Dispose();
            if (_ropeStats.IsCreated) _ropeStats.Dispose();

            if (_sphereCommands.IsCreated) _sphereCommands.Dispose();
            if (_rayHits.IsCreated) _rayHits.Dispose();
        }

        /// <summary>
        /// Updates the collider buffers with new data.
        /// </summary>
        public void UpdateColliders(NativeArray<SphereColliderData> spheres, NativeArray<PlaneColliderData> planes,
            NativeArray<CapsuleColliderData> capsules, NativeArray<BoxColliderData> boxes)
        {
            if (!_sphereColliders.IsCreated || _sphereColliders.Length != spheres.Length)
            {
                if (_sphereColliders.IsCreated) _sphereColliders.Dispose();
                _sphereColliders = new NativeArray<SphereColliderData>(spheres.Length, Allocator.Persistent);
            }
            _sphereColliders.CopyFrom(spheres);

            if (!_planeColliders.IsCreated || _planeColliders.Length != planes.Length)
            {
                if (_planeColliders.IsCreated) _planeColliders.Dispose();
                _planeColliders = new NativeArray<PlaneColliderData>(planes.Length, Allocator.Persistent);
            }
            _planeColliders.CopyFrom(planes);

            if (!_capsuleColliders.IsCreated || _capsuleColliders.Length != capsules.Length)
            {
                if (_capsuleColliders.IsCreated) _capsuleColliders.Dispose();
                _capsuleColliders = new NativeArray<CapsuleColliderData>(capsules.Length, Allocator.Persistent);
            }
            _capsuleColliders.CopyFrom(capsules);

            if (!_boxColliders.IsCreated || _boxColliders.Length != boxes.Length)
            {
                if (_boxColliders.IsCreated) _boxColliders.Dispose();
                _boxColliders = new NativeArray<BoxColliderData>(boxes.Length, Allocator.Persistent);
            }
            _boxColliders.CopyFrom(boxes);
        }

        /// <summary>
        /// Updates the pin constraints.
        /// </summary>
        public void UpdatePinConstraints(PinConstraintData[] pins)
        {
            if (_pinConstraints.IsCreated) _pinConstraints.Dispose();
            if (_pinImpulses.IsCreated) _pinImpulses.Dispose();

            _pinConstraints = new NativeArray<PinConstraintData>(pins, Allocator.Persistent);
            _pinImpulses = new NativeArray<float3>(pins.Length, Allocator.Persistent);
        }

        /// <summary>
        /// Updates the tether constraints.
        /// </summary>
        public void UpdateTethers(TetherConstraintData[] tethers)
        {
            if (_tetherConstraints.IsCreated) _tetherConstraints.Dispose();
            _tetherConstraints = new NativeArray<TetherConstraintData>(tethers, Allocator.Persistent);
        }

        /// <summary>
        /// Schedules a job to check if the rope should break due to stretching.
        /// </summary>
        public void ScheduleBreakCheck(float maxRatio, int explicitIndex = -1)
        {
            var breakJob = new BreakCheckJob
            {
                positions = _positions,
                breakIndex = _breakIndex,
                maxLenSq = (_segmentLength * maxRatio) * (_segmentLength * maxRatio),
                explicitBreakIndex = explicitIndex
            };
            _jobHandle = breakJob.Schedule(_jobHandle);
        }

        /// <summary>
        /// Schedules the Global Mesh Collision jobs: Prep -> SphereCast -> Resolve.
        /// </summary>
        public JobHandle ScheduleGlobalCollision(JobHandle inputDeps, float dt, LayerMask layerMask, float particleRadius, float friction)
        {
            int totalTasks = _particleCount * 2; // Particles + Segments

            // 1. Prepare: Generate SphereCastCommands
            var prepJob = new PrepareSphereCastCommandsJob
            {
                positions = _positions,
                prevPositions = _prevPositions,
                inverseMasses = _inverseMasses,
                commands = _sphereCommands,
                layerMask = layerMask,
                radius = particleRadius,
                minDistance = 1e-6f,
                particleCount = _particleCount
            };
            var prepHandle = prepJob.Schedule(totalTasks, 64, inputDeps);

            // 2. Batch SphereCast
            var castHandle = SpherecastCommand.ScheduleBatch(_sphereCommands, _rayHits, 64, prepHandle);

            // 3. Resolve Particle Collisions (Parallel)
            var resolveParticlesJob = new ResolveSphereCastCollisionsJob
            {
                positions = _positions,
                prevPositions = _prevPositions,
                inverseMasses = _inverseMasses,
                hits = _rayHits,
                radius = particleRadius,
                friction = friction
            };
            var resolveParticlesHandle = resolveParticlesJob.Schedule(_particleCount, 64, castHandle);

            // 4. Resolve Segment Collisions (Serial to avoid race conditions on shared nodes)
            var resolveSegmentsJob = new ResolveSegmentCollisionsJob
            {
                positions = _positions,
                prevPositions = _prevPositions,
                inverseMasses = _inverseMasses,
                hits = _rayHits,
                radius = particleRadius,
                friction = friction,
                particleCount = _particleCount
            };
            return resolveSegmentsJob.Schedule(resolveParticlesHandle);
        }
        /// <summary>
        /// Steps the physics simulation.
        /// </summary>
        public void Step(float dt, int subSteps, float3 gravity, float compliance, float friction, float particleRadius, float linearDamping, float bendingStiffness,
            float3 windVelocity = default, float turbulenceStrength = 0, float turbulenceFrequency = 0, float airDrag = 0, float time = 0, 
            NativeParallelMultiHashMap<int, GlobalSpatialEntry> globalSpatialMap = default, float globalCellSize = 0f, float interRopeStiffness = 0.5f, LayerMask collisionLayer = default, bool IsClosedLoop = false, 
            float maxVelocity = 100f, float massScale = 1.0f, JobHandle externalDependency = default, bool applyTwoWayCoupling = false)
        {
            float subStepDt = dt / subSteps;
            _jobHandle.Complete();

            // Store current positions for visual interpolation before simulation begins
            _jobHandle = new CopyPositionsJob
            {
                source = _positions,
                target = _lastFramePositions
            }.Schedule(_particleCount, 64);

            // Cleanup & Prep
            if (_pinImpulses.IsCreated)
            {
                _jobHandle = new ClearPinImpulsesJob { pinImpulses = _pinImpulses }.Schedule(_pinImpulses.Length, 64, _jobHandle);
            }

            _collisionEvents.Clear();
            _proxyEvents.Clear();
            _breakIndex.Value = -1;

            float cellSize = particleRadius * 2.0f;

            for (int i = 0; i < subSteps; i++)
            {
                // 1. Aerodynamics
                if (math.lengthsq(windVelocity) > 0 || turbulenceStrength > 0 || airDrag > 0)
                {
                    var aeroJob = new AerodynamicsJob
                    {
                        velocities = _velocities,
                        positions = _positions,
                        inverseMasses = _inverseMasses,
                        windVector = windVelocity,
                        turbulenceStrength = turbulenceStrength,
                        turbulenceFrequency = turbulenceFrequency,
                        airDrag = airDrag,
                        time = time + (i * subStepDt),
                        dt = subStepDt
                    };
                    _jobHandle = aeroJob.Schedule(_particleCount, 64, _jobHandle);
                }

                // 2. Integration
                var integrateJob = new IntegrateJob
                {
                    positions = _positions,
                    prevPositions = _prevPositions,
                    velocities = _velocities,
                    inverseMasses = _inverseMasses,
                    gravity = gravity,
                    dt = subStepDt
                };
                // Ensure IntegrateJob waits for any external reads (like GlobalMap population)
                var integrateDep = JobHandle.CombineDependencies(_jobHandle, externalDependency);
                _jobHandle = integrateJob.Schedule(_particleCount, 64, integrateDep);

                // 3. Spatial Hashing (for Self Collision)
                _jobHandle = new ClearSpatialMapJob { spatialMap = _spatialMap }.Schedule(_jobHandle);

                var populateJob = new PopulateSpatialMapJob
                {
                    positions = _positions,
                    spatialMapWriter = _spatialMap.AsParallelWriter(),
                    cellSize = cellSize
                };
                _jobHandle = populateJob.Schedule(_particleCount, 64, _jobHandle);

                // 4. Resolve Self Collisions
                var selfColJob = new ResolveSelfCollisionJob
                {
                    positions = _positions,
                    inverseMasses = _inverseMasses,
                    spatialMap = _spatialMap,
                    cellSize = cellSize,
                    radius = particleRadius,
                    stiffness = 1.0f
                };
                _jobHandle = selfColJob.Schedule(_particleCount, 64, _jobHandle);

                // 5. Environment Collision (Standard Proxies)
                if (_sphereColliders.Length > 0 || _planeColliders.Length > 0 || _capsuleColliders.Length > 0 || _boxColliders.Length > 0)
                {
                    var collisionJob = new RopeCollisionJob
                    {
                        positions = _positions,
                        prevPositions = _prevPositions,
                        inverseMasses = _inverseMasses,
                        sphereColliders = _sphereColliders,
                        planeColliders = _planeColliders,
                        capsuleColliders = _capsuleColliders,
                        boxColliders = _boxColliders,
                        eventWriter = _collisionEvents.AsParallelWriter(),
                        proxyEventWriter = _proxyEvents.AsParallelWriter(),
                        applyTwoWayCoupling = applyTwoWayCoupling,
                        friction = friction,
                        dt = subStepDt,
                        particleRadius = particleRadius
                    };
                    _jobHandle = collisionJob.Schedule(_particleCount, 64, _jobHandle);
                }

                // 6. Pin Constraints
                if (_pinConstraints.Length > 0)
                {
                    var pinJob = new PinConstraintJob
                    {
                        positions = _positions,
                        inverseMasses = _inverseMasses,
                        pinConstraints = _pinConstraints,
                        pinImpulses = _pinImpulses,
                        dt = subStepDt,
                        subStepFraction = (float)(i + 1) / subSteps
                    };
                    _jobHandle = pinJob.Schedule(_pinConstraints.Length, 32, _jobHandle);
                }

                // 7. Distance Constraints
                var constraintJob = new DistanceConstraintJob
                {
                    positions = _positions,
                    inverseMasses = _inverseMasses,
                    segmentLength = _segmentLength,
                    compliance = compliance,
                    dt = subStepDt,
                    iterations = 1,
                    massScale = massScale
                };
                _jobHandle = constraintJob.Schedule(_jobHandle);

                // 7b. Bending Constraints (Resists curling)
                if (bendingStiffness > 0)
                {
                    var bendingJob = new BendingConstraintJob
                    {
                        positions = _positions,
                        inverseMasses = _inverseMasses,
                        stiffness = bendingStiffness,
                        dt = subStepDt,
                        IsClosedLoop = IsClosedLoop
                    };
                    _jobHandle = bendingJob.Schedule(_jobHandle);
                }

                // 7c. Resolve Inter-Rope Collisions
                if (globalSpatialMap.IsCreated && globalCellSize > 0)
                {
                    var interRopeJob = new ResolveInterRopeCollisionJob
                    {
                        positions = _positions,
                        prevPositions = _prevPositions,
                        inverseMasses = _inverseMasses,
                        globalSpatialMap = globalSpatialMap,
                        cellSize = globalCellSize,
                        radius = particleRadius,
                        ropeId = RopeId,
                        friction = friction,
                        stiffness = interRopeStiffness
                    };
                    var combinedDep = JobHandle.CombineDependencies(_jobHandle, externalDependency);
                    _jobHandle = interRopeJob.Schedule(_particleCount, 64, combinedDep);
                }

                // 8. Tether Constraints
                if (_tetherConstraints.IsCreated && _tetherConstraints.Length > 0)
                {
                    var tetherJob = new TetherConstraintJob
                    {
                        positions = _positions,
                        inverseMasses = _inverseMasses,
                        tethers = _tetherConstraints
                    };
                    _jobHandle = tetherJob.Schedule(_tetherConstraints.Length, 64, _jobHandle);
                }

                // 8b. Global Mesh Collision (SphereCast)
                if (collisionLayer.value != 0)
                {
                    _jobHandle = ScheduleGlobalCollision(_jobHandle, subStepDt, collisionLayer, particleRadius, friction);
                }

                // 9. Velocity Update
                var velocityJob = new UpdateVelocityJob
                {
                    positions = _positions,
                    prevPositions = _prevPositions,
                    velocities = _velocities,
                    inverseMasses = _inverseMasses,
                    dt = subStepDt,
                    maxVelocity = maxVelocity,
                    damping = linearDamping
                };
                _jobHandle = velocityJob.Schedule(_particleCount, 64, _jobHandle);
            }

            // 10. Statistics
            var statsJob = new RopeStatisticsJob
            {
                positions = _positions,
                velocities = _velocities,
                stats = _ropeStats
            };
            _jobHandle = statsJob.Schedule(_jobHandle);
        }

        /// <summary>
        /// Forces job completion. Call before accessing NativeArrays.
        /// </summary>
        public void CompleteJobs()
        {
            _jobHandle.Complete();
        }

        /// <summary>
        /// Hashes a 3D position into a spatial grid index.
        /// </summary>
        public static int Hash(float3 position, float cellSize)
        {
            int3 gridPos = (int3)math.floor(position / cellSize);
            return (gridPos.x * 73856093) ^ (gridPos.y * 19349663) ^ (gridPos.z * 83492791);
        }

        /// <summary>
        /// Hashes a grid coordinate.
        /// </summary>
        public static int Hash(int3 gridPos)
        {
            return (gridPos.x * 73856093) ^ (gridPos.y * 19349663) ^ (gridPos.z * 83492791);
        }

        /// <summary>
        /// Teleports a particle to a specific position.
        /// </summary>
        public void SetParticlePosition(int index, Vector3 pos)
        {
            if (index >= 0 && index < _particleCount)
            {
                float3 p = new float3(pos.x, pos.y, pos.z);
                _positions[index] = p;
                _prevPositions[index] = p;
            }
        }

        /// <summary>
        /// Sets the velocity of a particle manually.
        /// </summary>
        public void SetVelocities(int index, Vector3 vel)
        {
            _velocities[index] = vel;
        }

        /// <summary>
        /// Adds a physical force to a particle (equivalent to Rigidbody.AddForce).
        /// Since XPBD uses velocities, force is applied as an acceleration (Force / Mass * dt).
        /// </summary>
        public void AddForce(int index, Vector3 force, float deltaTime = 0.016f)
        {
            if (index < 0 || index >= _particleCount) return;
            
            // a = F / m -> a = F * invMass
            float invMass = _inverseMasses[index];
            if (invMass <= 0f) return; // Kinematic/Pinned particle

            Vector3 acceleration = force * invMass;
            
            // dv = a * dt
            _velocities[index] += (float3)(acceleration * deltaTime);
        }

        /// <summary>
        /// Gets the current velocity of a particle.
        /// </summary>
        public Vector3 GetVelocity(int index)
        {
            if (index >= 0 && index < _particleCount) return _velocities[index];
            return Vector3.zero;
        }

        /// <summary>
        /// Gets the current position of a particle.
        /// </summary>
        public Vector3 GetPosition(int index)
        {
            if (index >= 0 && index < _particleCount) return _positions[index];
            return Vector3.zero;
        }

        /// <summary>
        /// Calculates the closest points between two line segments (p1-q1) and (p2-q2).
        /// </summary>
        public static void ClosestPointSegment(float3 p1, float3 q1, float3 p2, float3 q2,
            out float t1, out float t2, out float3 c1, out float3 c2)
        {
            float3 d1 = q1 - p1;
            float3 d2 = q2 - p2;
            float3 r = p1 - p2;
            float a = math.dot(d1, d1);
            float e = math.dot(d2, d2);
            float f = math.dot(d2, r);

            if (a <= 1e-6f && e <= 1e-6f)
            {
                t1 = 0;
                t2 = 0;
                c1 = p1;
                c2 = p2;
                return;
            }
            if (a <= 1e-6f)
            {
                t1 = 0;
                t2 = math.clamp(f / e, 0f, 1f);
                c1 = p1;
                c2 = p2 + d2 * t2;
                return;
            }
            if (e <= 1e-6f)
            {
                t2 = 0;
                t1 = math.clamp(-math.dot(d1, r) / a, 0f, 1f);
                c1 = p1 + d1 * t1;
                c2 = p2;
                return;
            }

            float c = math.dot(d1, r);
            float b = math.dot(d1, d2);
            float denom = a * e - b * b;

            if (denom != 0.0f)
                t1 = math.clamp((b * f - c * e) / denom, 0f, 1f);
            else
                t1 = 0.0f;

            float tNom = b * t1 + f;
            if (tNom < 0.0f)
            {
                t2 = 0.0f;
                t1 = math.clamp(-c / a, 0f, 1f);
            }
            else if (tNom > e)
            {
                t2 = 1.0f;
                t1 = math.clamp((b - c) / a, 0f, 1f);
            }
            else
            {
                t2 = tNom / e;
            }

            c1 = p1 + d1 * t1;
            c2 = p2 + d2 * t2;
        }

        /// <summary>
        /// Calculates the closest point on a segment [a, b] to a point p.
        /// </summary>
        public static float3 ClosestPointPointSegment(float3 p, float3 a, float3 b, out float t)
        {
            float3 ab = b - a;
            float dotAB = math.dot(ab, ab);
            if (dotAB < 1e-6f)
            {
                t = 0f;
                return a;
            }
            t = math.saturate(math.dot(p - a, ab) / dotAB);
            return a + t * ab;
        }
    }


    #region Job Definitions

    [BurstCompile]
    struct ClearPinImpulsesJob : IJobParallelFor
    {
        public NativeArray<float3> pinImpulses;
        public void Execute(int i) => pinImpulses[i] = float3.zero;
    }

    [BurstCompile]
    struct CopyPositionsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> source;
        public NativeArray<float3> target;
        public void Execute(int i) => target[i] = source[i];
    }

    [BurstCompile]
    struct IntegrateJob : IJobParallelFor
    {
        public NativeArray<float3> positions;
        public NativeArray<float3> prevPositions;
        public NativeArray<float3> velocities;
        [ReadOnly] public NativeArray<float> inverseMasses;
        public float3 gravity;
        public float dt;

        public void Execute(int i)
        {
            // Sync previous position even for kinematic particles to ensure 
            // the velocity calculation (pos - prevPos) / dt only reflects current frame delta.
            prevPositions[i] = positions[i];

            if (inverseMasses[i] == 0) return;
            
            // Apply gravity only
            velocities[i] += gravity * dt;
            positions[i] += velocities[i] * dt;
        }
    }

    [BurstCompile]
    struct UpdateVelocityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> prevPositions;
        public NativeArray<float3> velocities;
        [ReadOnly] public NativeArray<float> inverseMasses;
        public float dt;
        public float maxVelocity;
        public float damping;

        public void Execute(int i)
        {
            if (inverseMasses[i] == 0)
            {
                velocities[i] = (positions[i] - prevPositions[i]) / dt;
                return;
            }

            float3 v = (positions[i] - prevPositions[i]) / dt;
            
            // Accurate Global Damping: Apply damping to the final velocity AFTER all constraints
            v *= math.max(0f, 1f - (damping * 50f) * dt);

            float speed = math.length(v);
            if (speed > maxVelocity && speed > 1e-6f)
            {
                v = (v / speed) * maxVelocity;
            }
            velocities[i] = v;
        }
    }

    [BurstCompile]
    struct DistanceConstraintJob : IJob
    {
        public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        public float segmentLength;
        public float compliance;
        public float dt;
        public int iterations;
        public float massScale;
        public bool IsClosedLoop;

        public void Execute()
        {
            float alphaTilde = compliance / (dt * dt);
            for (int k = 0; k < iterations; k++)
            {
                int count = positions.Length - 1;
                for (int i = 0; i < count; i++)
                {
                    ApplyConstraint(i, i + 1, alphaTilde);
                }

                if (IsClosedLoop)
                {
                    ApplyConstraint(positions.Length - 1, 0, alphaTilde);
                }
            }
        }

        private void ApplyConstraint(int p1, int p2, float alphaTilde)
        {
            float w1 = inverseMasses[p1];
            float w2 = inverseMasses[p2];

            if (massScale > 1.001f)
            {
                w1 *= math.pow(massScale, p1);
                w2 *= math.pow(massScale, p2);
            }

            float wSum = w1 + w2;

            if (wSum == 0) return;

            float3 n = positions[p1] - positions[p2];
            float dist = math.length(n);
            if (dist < 1e-6f) return;

            n /= dist;
            float C = dist - segmentLength;
            float deltaLambda = -C / (wSum + alphaTilde);
            float3 deltaX = deltaLambda * n;

            if (w1 > 0) positions[p1] += deltaX * w1;
            if (w2 > 0) positions[p2] -= deltaX * w2;
        }
    }

    [BurstCompile]
    struct PinConstraintJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [ReadOnly] public NativeArray<PinConstraintData> pinConstraints;
        [NativeDisableParallelForRestriction] public NativeArray<float3> pinImpulses;
        public float dt;
        public float subStepFraction;

        public void Execute(int i)
        {
            PinConstraintData pin = pinConstraints[i];
            int pIdx = pin.ParticleIndex;
            float w_p = inverseMasses[pIdx];

            // Interpolate target position for this sub-step to keep velocity consistent
            float3 currentTarget = math.lerp(pin.StartPosition, pin.TargetPosition, subStepFraction);

            float3 diff = positions[pIdx] - currentTarget;
            float dist = math.length(diff);
            if (dist < 1e-6f) return;

            float3 n = diff / dist;
            float w_b = 0f;

            if (pin.BodyInverseMass > 0f)
            {
                float3 r = currentTarget - pin.BodyPosition;
                float3 rn = math.cross(r, n);
                w_b = pin.BodyInverseMass + math.dot(rn, math.mul(pin.BodyInverseInertia, rn));
            }

            float alpha = pin.Compliance / (dt * dt);
            float wSum = w_p + w_b + alpha;

            if (wSum <= 0.0f)
            {
                // Hard pin to static/kinematic target
                positions[pIdx] = currentTarget;
                return;
            }

            float deltaLambda = -dist / wSum;

            if (w_p > 0)
            {
                positions[pIdx] += deltaLambda * n * w_p;
            }
            else
            {
                // Particle is kinematic (infinite mass), so it stays exactly at target
                positions[pIdx] = currentTarget;
            }

            pinImpulses[i] += deltaLambda * n;
        }
    }

    [BurstCompile]
    struct RopeCollisionJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> positions;
        [NativeDisableParallelForRestriction] public NativeArray<float3> prevPositions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [ReadOnly] public NativeArray<SphereColliderData> sphereColliders;
        [ReadOnly] public NativeArray<PlaneColliderData> planeColliders;
        [ReadOnly] public NativeArray<CapsuleColliderData> capsuleColliders;
        [ReadOnly] public NativeArray<BoxColliderData> boxColliders;

        [WriteOnly] public NativeQueue<CollisionEvent>.ParallelWriter eventWriter;
        [WriteOnly] public NativeQueue<ProxyCollisionEvent>.ParallelWriter proxyEventWriter;
        public bool applyTwoWayCoupling;
        public float friction;
        public float dt;
        public float particleRadius;

        public void Execute(int i)
        {
            if (inverseMasses[i] == 0) return;
            float3 pos = positions[i];
            bool collided = false;

            // Planes
            for (int c = 0; c < planeColliders.Length; c++)
            {
                PlaneColliderData p = planeColliders[c];
                float signedDist = math.dot(pos - p.PointOnPlane, p.Normal) - particleRadius;
                if (signedDist < 0)
                {
                    float penetration = -signedDist;
                    float3 n = p.Normal;
                    pos += n * penetration;
                    ApplyFriction(i, ref pos, n, penetration);
                    DampNormalVelocity(i, pos, n);
                    collided = true;
                    
                    if (applyTwoWayCoupling)
                    {
                        float m = 1.0f / inverseMasses[i];
                        float3 imp = n * penetration * m / dt;
                        proxyEventWriter.Enqueue(new ProxyCollisionEvent { ProxyType = 3, ProxyIndex = c, Impulse = -imp, Point = positions[i] });
                    }
                }
            }

            positions[i] = pos;

            if (i < positions.Length - 1 && (inverseMasses[i] > 0 || inverseMasses[i + 1] > 0))
            {
                float3 p0 = positions[i];
                float3 p1 = positions[i + 1];

                // Spheres
                for (int c = 0; c < sphereColliders.Length; c++)
                {
                    SphereColliderData s = sphereColliders[c];

                    float t;
                    float3 closestOnSeg = RopeSolver.ClosestPointPointSegment(s.Position, p0, p1, out t);
                    float3 dir = closestOnSeg - s.Position;
                    float dist = math.length(dir);
                    float combinedRad = s.Radius + particleRadius;

                    if (dist < combinedRad)
                    {
                        if (dist < 1e-5f) { dir = new float3(0, 1, 0); dist = 1e-5f; }

                        float3 n = dir / dist;
                        float penetration = combinedRad - dist;
                        float3 correction = n * penetration;

                        if (inverseMasses[i] > 0) 
                        {
                            positions[i] += correction * (1.0f - t);
                            
                            float3 temp = positions[i];
                            ApplyFriction(i, ref temp, n, penetration * (1.0f - t));
                            positions[i] = temp;
                            
                            DampNormalVelocity(i, positions[i], n);
                        }
                        if (inverseMasses[i + 1] > 0) 
                        {
                            positions[i + 1] += correction * t;
                            
                            float3 temp = positions[i + 1];
                            ApplyFriction(i + 1, ref temp, n, penetration * t);
                            positions[i + 1] = temp;
                            
                            DampNormalVelocity(i + 1, positions[i + 1], n);
                        }

                        if (applyTwoWayCoupling)
                        {
                            if (inverseMasses[i] > 0)
                            {
                                float m = 1.0f / inverseMasses[i];
                                float3 imp = correction * (1.0f - t) * m / dt;
                                proxyEventWriter.Enqueue(new ProxyCollisionEvent { ProxyType = 0, ProxyIndex = c, Impulse = -imp, Point = positions[i] });
                            }
                            if (inverseMasses[i + 1] > 0)
                            {
                                float m = 1.0f / inverseMasses[i + 1];
                                float3 imp = correction * t * m / dt;
                                proxyEventWriter.Enqueue(new ProxyCollisionEvent { ProxyType = 0, ProxyIndex = c, Impulse = -imp, Point = positions[i + 1] });
                            }
                        }

                        collided = true;

                        p0 = positions[i];
                        p1 = positions[i + 1];
                    }
                }

                // Capsules
                for (int c = 0; c < capsuleColliders.Length; c++)
                {
                    CapsuleColliderData cap = capsuleColliders[c];

                    float t1, t2;
                    float3 c1, c2;
                    RopeSolver.ClosestPointSegment(p0, p1, cap.Start, cap.End, out t1, out t2, out c1, out c2);

                    float3 dir = c1 - c2;
                    float dist = math.length(dir);
                    float combinedRad = cap.Radius + particleRadius;

                    if (dist < combinedRad)
                    {
                        if (dist < 1e-5f) { dir = new float3(0, 1, 0); dist = 1e-5f; }

                        float3 n = dir / dist;
                        float penetration = combinedRad - dist;
                        float3 correction = n * penetration;

                        if (inverseMasses[i] > 0) 
                        {
                            positions[i] += correction * (1.0f - t1);
                            
                            float3 temp = positions[i];
                            ApplyFriction(i, ref temp, n, penetration * (1.0f - t1));
                            positions[i] = temp;
                            
                            DampNormalVelocity(i, positions[i], n);
                        }
                        if (inverseMasses[i + 1] > 0) 
                        {
                            positions[i + 1] += correction * t1;
                            
                            float3 temp = positions[i + 1];
                            ApplyFriction(i + 1, ref temp, n, penetration * t1);
                            positions[i + 1] = temp;
                            
                            DampNormalVelocity(i + 1, positions[i + 1], n);
                        }

                        if (applyTwoWayCoupling)
                        {
                            if (inverseMasses[i] > 0)
                            {
                                float m = 1.0f / inverseMasses[i];
                                float3 imp = correction * (1.0f - t1) * m / dt;
                                proxyEventWriter.Enqueue(new ProxyCollisionEvent { ProxyType = 1, ProxyIndex = c, Impulse = -imp, Point = positions[i] });
                            }
                            if (inverseMasses[i + 1] > 0)
                            {
                                float m = 1.0f / inverseMasses[i + 1];
                                float3 imp = correction * t1 * m / dt;
                                proxyEventWriter.Enqueue(new ProxyCollisionEvent { ProxyType = 1, ProxyIndex = c, Impulse = -imp, Point = positions[i + 1] });
                            }
                        }

                        collided = true;
                        p0 = positions[i];
                        p1 = positions[i + 1];
                    }
                }

                // Boxes
                for (int c = 0; c < boxColliders.Length; c++)
                {
                    BoxColliderData box = boxColliders[c];

                    float3 localP0 = math.rotate(math.inverse(box.Rotation), p0 - box.Center);
                    float3 localP1 = math.rotate(math.inverse(box.Rotation), p1 - box.Center);

                    float3 halfSize = box.Size * 0.5f;
                    float3 expandedHalfSize = halfSize + particleRadius;

                    float3 minSeg = math.min(localP0, localP1);
                    float3 maxSeg = math.max(localP0, localP1);

                    if (minSeg.x > expandedHalfSize.x || maxSeg.x < -expandedHalfSize.x ||
                        minSeg.y > expandedHalfSize.y || maxSeg.y < -expandedHalfSize.y ||
                        minSeg.z > expandedHalfSize.z || maxSeg.z < -expandedHalfSize.z)
                    {
                        continue;
                    }

                    float t;
                    float3 closestToCenter = RopeSolver.ClosestPointPointSegment(float3.zero, localP0, localP1, out t);

                    if (math.abs(closestToCenter.x) < expandedHalfSize.x &&
                        math.abs(closestToCenter.y) < expandedHalfSize.y &&
                        math.abs(closestToCenter.z) < expandedHalfSize.z)
                    {
                        float3 distToEdge = expandedHalfSize - math.abs(closestToCenter);
                        float3 normalLocal;
                        float penetration;

                        if (distToEdge.x < distToEdge.y && distToEdge.x < distToEdge.z)
                        {
                            normalLocal = new float3(math.sign(closestToCenter.x), 0, 0);
                            penetration = distToEdge.x;
                        }
                        else if (distToEdge.y < distToEdge.z)
                        {
                            normalLocal = new float3(0, math.sign(closestToCenter.y), 0);
                            penetration = distToEdge.y;
                        }
                        else
                        {
                            normalLocal = new float3(0, 0, math.sign(closestToCenter.z));
                            penetration = distToEdge.z;
                        }

                        if (penetration > 0)
                        {
                            float3 correctionLocal = normalLocal * penetration;
                            float3 correctionWorld = math.rotate(box.Rotation, correctionLocal);
                            float3 normalWorld = math.rotate(box.Rotation, normalLocal);

                            if (inverseMasses[i] > 0) 
                            {
                                positions[i] += correctionWorld * (1.0f - t);
                                
                                float3 temp = positions[i];
                                ApplyFriction(i, ref temp, normalWorld, penetration * (1.0f - t));
                                positions[i] = temp;
                                
                                DampNormalVelocity(i, positions[i], normalWorld);
                            }
                            if (inverseMasses[i + 1] > 0) 
                            {
                                positions[i + 1] += correctionWorld * t;
                                
                                float3 temp = positions[i + 1];
                                ApplyFriction(i + 1, ref temp, normalWorld, penetration * t);
                                positions[i + 1] = temp;
                                
                                DampNormalVelocity(i + 1, positions[i + 1], normalWorld);
                            }

                            if (applyTwoWayCoupling)
                            {
                                if (inverseMasses[i] > 0)
                                {
                                    float m = 1.0f / inverseMasses[i];
                                    float3 imp = correctionWorld * (1.0f - t) * m / dt;
                                    proxyEventWriter.Enqueue(new ProxyCollisionEvent { ProxyType = 2, ProxyIndex = c, Impulse = -imp, Point = positions[i] });
                                }
                                if (inverseMasses[i + 1] > 0)
                                {
                                    float m = 1.0f / inverseMasses[i + 1];
                                    float3 imp = correctionWorld * t * m / dt;
                                    proxyEventWriter.Enqueue(new ProxyCollisionEvent { ProxyType = 2, ProxyIndex = c, Impulse = -imp, Point = positions[i + 1] });
                                }
                            }

                            collided = true;
                            p0 = positions[i];
                            p1 = positions[i + 1];
                        }
                    }
                }
            }

            if (collided)
            {
                float mag = math.length(positions[i] - prevPositions[i]) / dt;
                if (mag > 0.5f) eventWriter.Enqueue(new CollisionEvent { Position = positions[i], VelocityMagnitude = mag });
            }
        }

        private void ApplyFriction(int i, ref float3 pos, float3 normal, float penetration)
        {
            float3 prev = prevPositions[i];
            float3 delta = pos - prev;
            float3 tan = delta - math.dot(delta, normal) * normal;
            float tanLen = math.length(tan);
            if (tanLen > 1e-6f)
            {
                // Coulomb friction limit: mu * normal_force
                float limit = friction * penetration;
                float correction = math.min(limit, tanLen);
                pos -= (tan / tanLen) * correction;
            }
        }

        private void DampNormalVelocity(int i, float3 pos, float3 normal)
        {
            float3 prev = prevPositions[i];
            float3 delta = pos - prev;
            float vNormal = math.dot(delta, normal);
            prevPositions[i] += vNormal * normal;
        }
    }

    [BurstCompile]
    struct ClearSpatialMapJob : IJob
    {
        public NativeParallelMultiHashMap<int, RopeSolver.SpatialEntry> spatialMap;
        public void Execute() => spatialMap.Clear();
    }

    [BurstCompile]
    struct PopulateSpatialMapJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        [WriteOnly] public NativeParallelMultiHashMap<int, RopeSolver.SpatialEntry>.ParallelWriter spatialMapWriter;
        public float cellSize;

        public void Execute(int i)
        {
            spatialMapWriter.Add(RopeSolver.Hash(positions[i], cellSize),
                new RopeSolver.SpatialEntry { Index = i, IsVirtual = false });
            if (i < positions.Length - 1)
            {
                float3 mid = (positions[i] + positions[i + 1]) * 0.5f;
                spatialMapWriter.Add(RopeSolver.Hash(mid, cellSize),
                    new RopeSolver.SpatialEntry { Index = i, IsVirtual = true });
            }
        }
    }

    [BurstCompile]
    struct ResolveSelfCollisionJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [ReadOnly] public NativeParallelMultiHashMap<int, RopeSolver.SpatialEntry> spatialMap;
        public float cellSize;
        public float radius;
        public float stiffness;

        public void Execute(int i)
        {
            if (inverseMasses[i] == 0) return;
            Resolve(i, false);
            if (i < positions.Length - 1) Resolve(i, true);
        }

        void Resolve(int idx, bool isVirtual)
        {
            float3 selfPos = isVirtual ? (positions[idx] + positions[idx + 1]) * 0.5f : positions[idx];
            int3 grid = (int3)math.floor(selfPos / cellSize);
            float colDistSq = (radius * 2) * (radius * 2);

            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        int hash = RopeSolver.Hash(selfPos + new float3(x, y, z) * cellSize, cellSize);
                        if (spatialMap.TryGetFirstValue(hash, out RopeSolver.SpatialEntry other, out var it))
                        {
                            do
                            {
                                float selfF = isVirtual ? idx + 0.5f : idx;
                                float otherF = other.IsVirtual ? other.Index + 0.5f : other.Index;
                                if (math.abs(selfF - otherF) < 3.5f) continue;

                                float3 otherPos = other.IsVirtual
                                    ? (positions[other.Index] + positions[other.Index + 1]) * 0.5f
                                    : positions[other.Index];
                                float3 dir = selfPos - otherPos;
                                float dSq = math.lengthsq(dir);

                                if (dSq < colDistSq && dSq > 1e-6f)
                                {
                                    float d = math.sqrt(dSq);
                                    float3 corr = (dir / d) * (radius * 2 - d) * 0.5f * stiffness;
                                    if (isVirtual)
                                    {
                                        positions[idx] += corr * 0.5f;
                                        positions[idx + 1] += corr * 0.5f;
                                    }
                                    else positions[idx] += corr;
                                }
                            } while (spatialMap.TryGetNextValue(out other, ref it));
                        }
                    }
        }
    }

    [BurstCompile]
    struct TetherConstraintJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [ReadOnly] public NativeArray<TetherConstraintData> tethers;

        public void Execute(int i)
        {
            var t = tethers[i];
            if (inverseMasses[t.ParticleIndex] == 0) return;
            float3 dir = positions[t.ParticleIndex] - positions[t.AnchorParticleIndex];
            float len = math.length(dir);
            if (len > t.MaxLength && len > 1e-6f)
            {
                positions[t.ParticleIndex] += (dir / len) * (t.MaxLength - len);
            }
        }
    }

    [BurstCompile]
    struct AerodynamicsJob : IJobParallelFor
    {
        public NativeArray<float3> velocities;
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        public float3 windVector;
        public float turbulenceStrength;
        public float turbulenceFrequency;
        public float airDrag;
        public float time;
        public float dt;

        public void Execute(int i)
        {
            if (inverseMasses[i] == 0) return;
            velocities[i] *= math.max(0f, 1f - airDrag * dt);

            float3 noisePos = positions[i] * turbulenceFrequency;
            noisePos.y -= time;
            float3 turb = float3.zero;
            if (turbulenceStrength > 0)
            {
                turb = new float3(noise.snoise(noisePos), noise.snoise(noisePos + new float3(100, 0, 0)),
                    noise.snoise(noisePos + new float3(0, 100, 0))) * turbulenceStrength;
            }

            velocities[i] += (windVector + turb) * inverseMasses[i] * dt;
        }
    }

    [BurstCompile]
    struct BreakCheckJob : IJob
    {
        [ReadOnly] public NativeArray<float3> positions;
        public NativeReference<int> breakIndex;
        public float maxLenSq;
        public int explicitBreakIndex;

        public void Execute()
        {
            if (explicitBreakIndex >= 0 && explicitBreakIndex < positions.Length - 1)
            {
                float dSq = math.lengthsq(positions[explicitBreakIndex] - positions[explicitBreakIndex + 1]);
                if (dSq > maxLenSq) breakIndex.Value = explicitBreakIndex;
                return;
            }

            float maxDistSq = -1f;
            int bestIdx = -1;
            for (int i = 0; i < positions.Length - 1; i++)
            {
                float dSq = math.lengthsq(positions[i] - positions[i + 1]);
                if (dSq > maxDistSq)
                {
                    maxDistSq = dSq;
                    bestIdx = i;
                }
            }

            if (bestIdx != -1 && maxDistSq > maxLenSq) breakIndex.Value = bestIdx;
        }
    }

    [BurstCompile]
    struct RopeStatisticsJob : IJob
    {
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> velocities;
        public NativeReference<float2> stats;

        public void Execute()
        {
            float len = 0, vel = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                vel += math.length(velocities[i]);
                if (i < positions.Length - 1) len += math.distance(positions[i], positions[i + 1]);
            }

            stats.Value = new float2(len, vel);
        }
    }

    [BurstCompile]
    public struct PopulateGlobalMapJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> prevPositions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [WriteOnly] public NativeParallelMultiHashMap<int, RopeSolver.GlobalSpatialEntry>.ParallelWriter globalSpatialMap;
        public float cellSize;
        public float radius;
        public int ropeId;

        public void Execute(int i)
        {
            if (i >= positions.Length - 1) return;

            float3 p0 = positions[i];
            float3 p1 = positions[i + 1];
            float3 prev0 = prevPositions[i];
            float3 prev1 = prevPositions[i + 1];

            // Swept AABB: Include both current and previous positions to capture fast-moving ropes
            float3 minPos = math.min(math.min(p0, p1), math.min(prev0, prev1)) - radius;
            float3 maxPos = math.max(math.max(p0, p1), math.max(prev0, prev1)) + radius;

            int3 minCell = (int3)math.floor(minPos / cellSize);
            int3 maxCell = (int3)math.floor(maxPos / cellSize);

            // Hard cap to prevent HashMap explosion / infinite loop if a segment is extremely stretched
            maxCell = math.min(maxCell, minCell + new int3(3, 3, 3));

            var entry = new RopeSolver.GlobalSpatialEntry
            {
                RopeId = ropeId,
                Position = p0,
                EndPosition = p1,
                PrevPosition = prev0,
                PrevEndPosition = prev1,
                Radius = radius,
                InverseMass0 = inverseMasses[i],
                InverseMass1 = inverseMasses[i + 1]
            };

            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                for (int y = minCell.y; y <= maxCell.y; y++)
                {
                    for (int z = minCell.z; z <= maxCell.z; z++)
                    {
                        globalSpatialMap.Add(RopeSolver.Hash(new int3(x, y, z)), entry);
                    }
                }
            }
        }
    }

    [BurstCompile]
    struct ResolveInterRopeCollisionJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> positions;
        [NativeDisableParallelForRestriction] public NativeArray<float3> prevPositions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [ReadOnly] public NativeParallelMultiHashMap<int, RopeSolver.GlobalSpatialEntry> globalSpatialMap;
        public float cellSize;
        public float radius;
        public int ropeId;
        public float friction;
        public float stiffness;

        public void Execute(int i)
        {
            if (i >= positions.Length - 1) return;
            if (inverseMasses[i] == 0 && inverseMasses[i + 1] == 0) return;

            float3 p0 = positions[i];
            float3 p1 = positions[i + 1];
            float3 prev0 = prevPositions[i];
            float3 prev1 = prevPositions[i + 1];

            // Swept AABB of My Segment
            float3 minPos = math.min(math.min(p0, p1), math.min(prev0, prev1)) - radius;
            float3 maxPos = math.max(math.max(p0, p1), math.max(prev0, prev1)) + radius;
            int3 minCell = (int3)math.floor(minPos / cellSize);
            int3 maxCell = (int3)math.floor(maxPos / cellSize);

            float combinedRad = radius * 2.0f;
            float colDistSq = combinedRad * combinedRad;

            for (int x = minCell.x; x <= maxCell.x; x++)
                for (int y = minCell.y; y <= maxCell.y; y++)
                    for (int z = minCell.z; z <= maxCell.z; z++)
                    {
                        int hash = RopeSolver.Hash(new int3(x, y, z));
                        if (globalSpatialMap.TryGetFirstValue(hash, out RopeSolver.GlobalSpatialEntry other, out var it))
                        {
                            do
                            {
                                if (other.RopeId == ropeId) continue;

                                // 1. Midpoint Check
                                float3 midP0 = (p0 + prev0) * 0.5f;
                                float3 midP1 = (p1 + prev1) * 0.5f;
                                float3 otherMidP0 = (other.Position + other.PrevPosition) * 0.5f;
                                float3 otherMidP1 = (other.EndPosition + other.PrevEndPosition) * 0.5f;

                                float t1, t2;
                                float3 c1, c2;
                                RopeSolver.ClosestPointSegment(midP0, midP1, otherMidP0, otherMidP1, out t1, out t2, out c1, out c2);
                                
                                float distSq = math.lengthsq(c1 - c2);
                                bool collisionDetected = false;
                                float3 n = float3.zero;
                                float dist = 0;

                                if (distSq < colDistSq && distSq > 1e-9f)
                                {
                                    collisionDetected = true;
                                    dist = math.sqrt(distSq);
                                    n = (c1 - c2) / dist;
                                }
                                else
                                {
                                    // 2. Fallback to Current Check
                                    RopeSolver.ClosestPointSegment(p0, p1, other.Position, other.EndPosition, out t1, out t2, out c1, out c2);
                                    distSq = math.lengthsq(c1 - c2);
                                    if (distSq < colDistSq && distSq > 1e-9f)
                                    {
                                        collisionDetected = true;
                                        dist = math.sqrt(distSq);
                                        n = (c1 - c2) / dist;
                                    }
                                }

                                if (collisionDetected)
                                {
                                    // Calculate overlap at *current* positions using the detected normal
                                    float3 currentC1 = p0 + (p1 - p0) * t1;
                                    float3 currentC2 = other.Position + (other.EndPosition - other.Position) * t2;
                                    float currentProjDist = math.dot(currentC1 - currentC2, n);
                                    
                                    // If they crossed, currentProjDist will be negative! We still want to separate them.
                                    float overlap = combinedRad - currentProjDist;

                                    if (overlap > 0)
                                    {
                                        // Mass-weighted correction
                                        float wA = math.lerp(inverseMasses[i], inverseMasses[i + 1], t1);
                                        float wB = math.lerp(other.InverseMass0, other.InverseMass1, t2);
                                        float wSum = wA + wB;

                                        if (wSum > 1e-6f)
                                        {
                                            float3 correction = n * (overlap * (wA / wSum) * stiffness);

                                            if (inverseMasses[i] > 0) positions[i] += correction * (1.0f - t1);
                                            if (inverseMasses[i + 1] > 0) positions[i + 1] += correction * t1;

                                            // Apply Friction (Coulomb) using Relative Velocity
                                            float3 velA0 = p0 - prev0; 
                                            float3 velA1 = p1 - prev1;
                                            float3 velA = math.lerp(velA0, velA1, t1);

                                            float3 velB0 = other.Position - other.PrevPosition;
                                            float3 velB1 = other.EndPosition - other.PrevEndPosition;
                                            float3 velB = math.lerp(velB0, velB1, t2);

                                            float3 relativeVel = velA - velB;

                                            float3 tangent = relativeVel - math.dot(relativeVel, n) * n;
                                            float tanLen = math.length(tangent);
                                            float3 frictionImpulse = float3.zero;

                                            if (tanLen > 1e-6f)
                                            {
                                                float limit = friction * (overlap * (wA / wSum) * stiffness);
                                                float corr = math.min(limit, tanLen);
                                                frictionImpulse = -(tangent / tanLen) * corr;
                                            }

                                            if (inverseMasses[i] > 0)
                                            {
                                                positions[i] += frictionImpulse * (1.0f - t1);
                                                float3 d0 = positions[i] - prevPositions[i];
                                                float vN0 = math.dot(d0, n);
                                                if (vN0 < 0) prevPositions[i] += vN0 * n * 0.5f;
                                            }
                                            if (inverseMasses[i + 1] > 0)
                                            {
                                                positions[i + 1] += frictionImpulse * t1;
                                                float3 d1 = positions[i + 1] - prevPositions[i + 1];
                                                float vN1 = math.dot(d1, n);
                                                if (vN1 < 0) prevPositions[i + 1] += vN1 * n * 0.5f;
                                            }

                                            // Update local p0/p1 for next iteration
                                            p0 = positions[i];
                                            p1 = positions[i + 1];
                                        }
                                    }
                                }
                            } while (globalSpatialMap.TryGetNextValue(out other, ref it));
                        }
                    }
        }

    }

    // --- Global Mesh Collision Jobs (SphereCast) ---

    [BurstCompile]
    struct PrepareSphereCastCommandsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> prevPositions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [WriteOnly] public NativeArray<SpherecastCommand> commands;
        public int layerMask;
        public float radius;
        public float minDistance;
        public int particleCount;

        public void Execute(int i)
        {
            // Case 1: Particle Motion Cast (Index 0 to particleCount - 1)
            if (i < particleCount)
            {
                if (inverseMasses[i] == 0)
                {
                    commands[i] = new SpherecastCommand(Vector3.zero, 0f, Vector3.forward, new QueryParameters(0), 0f);
                    return;
                }

                float3 diff = positions[i] - prevPositions[i];
                float dist = math.length(diff);
                float castDist = math.max(dist, minDistance);
                float3 dir = (dist > 1e-9f) ? (diff / dist) : new float3(0, -1, 0);

                float retraction = 0.01f;
                float3 origin = prevPositions[i] - dir * retraction;
                float adjustedDist = castDist + retraction;

                QueryParameters queryParams = new QueryParameters(layerMask);
                commands[i] = new SpherecastCommand(origin, radius, dir, queryParams, adjustedDist);
            }
            // Case 2: Segment Cast (Index particleCount to 2*particleCount - 1)
            else
            {
                int segIdx = i - particleCount;
                // Only create casts for valid segments (0 to N-2)
                if (segIdx >= particleCount - 1)
                {
                    commands[i] = new SpherecastCommand(Vector3.zero, 0f, Vector3.forward, new QueryParameters(0), 0f);
                    return;
                }

                // Check mass of both ends
                if (inverseMasses[segIdx] == 0 && inverseMasses[segIdx + 1] == 0)
                {
                    commands[i] = new SpherecastCommand(Vector3.zero, 0f, Vector3.forward, new QueryParameters(0), 0f);
                    return;
                }

                float3 p0 = positions[segIdx];
                float3 p1 = positions[segIdx + 1];
                float3 diff = p1 - p0;
                float dist = math.length(diff);

                if (dist < 1e-6f)
                {
                    commands[i] = new SpherecastCommand(Vector3.zero, 0f, Vector3.forward, new QueryParameters(0), 0f);
                    return;
                }

                float3 dir = diff / dist;
                QueryParameters queryParams = new QueryParameters(layerMask);

                // Cast from p0 towards p1 to check the segment volume
                commands[i] = new SpherecastCommand(p0, radius, dir, queryParams, dist);
            }
        }
    }

    [BurstCompile]
    struct ResolveSphereCastCollisionsJob : IJobParallelFor
    {
        public NativeArray<float3> positions;
        public NativeArray<float3> prevPositions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [ReadOnly] public NativeArray<RaycastHit> hits;
        public float radius;
        public float friction;

        public void Execute(int i)
        {
            if (inverseMasses[i] == 0) return;

            RaycastHit hit = hits[i];

#if UNITY_6000_3_OR_NEWER
            if (hit.colliderEntityId != default)
#else
            if (hit.colliderInstanceID != 0)
#endif
            {
                float3 normal = hit.normal;

                // Safety check: ensure normal is valid
                if (math.lengthsq(normal) < 1e-6f) return;

                float3 hitPoint = hit.point;
                float penetration = radius + 0.001f; // approximation
                float3 newPos = hitPoint + normal * penetration;

                // Friction
                float3 prev = prevPositions[i];
                float3 delta = newPos - prev;
                float3 tan = delta - math.dot(delta, normal) * normal;
                float tanLen = math.length(tan);
                if (tanLen > 1e-6f)
                {
                    float limit = friction * penetration;
                    float correction = math.min(limit, tanLen);
                    newPos -= (tan / tanLen) * correction;
                }

                positions[i] = newPos;

                // Velocity Damping (Inelastic Collision)
                float3 finalDelta = newPos - prev;
                float vNormal = math.dot(finalDelta, normal);
                prevPositions[i] += vNormal * normal;
            }
        }
    }

    [BurstCompile]
    struct ResolveSegmentCollisionsJob : IJob
    {
        [NativeDisableParallelForRestriction] public NativeArray<float3> positions;
        [NativeDisableParallelForRestriction] public NativeArray<float3> prevPositions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        [ReadOnly] public NativeArray<RaycastHit> hits;
        public float radius;
        public float friction;
        public int particleCount;

        public void Execute()
        {
            // Iterate over segment hits (offset by particleCount)
            for (int i = particleCount; i < hits.Length; i++)
            {
                int segIdx = i - particleCount;
                if (segIdx >= particleCount - 1) continue;

                RaycastHit hit = hits[i];
#if UNITY_6000_3_OR_NEWER
                if (hit.colliderEntityId != default)
#else
                if (hit.colliderInstanceID != 0)
#endif
                {
                    float3 normal = hit.normal;
                    if (math.lengthsq(normal) < 1e-6f) continue;

                    float3 p0 = positions[segIdx];
                    float3 p1 = positions[segIdx + 1];
                    float dist = math.distance(p0, p1);
                    if (dist < 1e-6f) continue;

                    // Calculate interpolation factor t based on hit distance along segment
                    float t = math.clamp(hit.distance / dist, 0f, 1f);

                    float pushDist = 0.001f;
                    float penetration = pushDist;
                    float3 correction = normal * pushDist;

                    float w0 = inverseMasses[segIdx];
                    float w1 = inverseMasses[segIdx + 1];

                    if (w0 > 0) positions[segIdx] += correction * (1.0f - t);
                    if (w1 > 0) positions[segIdx + 1] += correction * t;

                    // Apply friction
                    float3 prev0 = prevPositions[segIdx];
                    float3 prev1 = prevPositions[segIdx + 1];
                    float3 v0 = positions[segIdx] - prev0;
                    float3 v1 = positions[segIdx + 1] - prev1;

                    float3 vPoint = math.lerp(v0, v1, t);
                    float3 tan = vPoint - math.dot(vPoint, normal) * normal;
                    float tanLen = math.length(tan);

                    if (tanLen > 1e-6f)
                    {
                        float limit = friction * penetration;
                        float fricCorr = math.min(limit, tanLen);
                        float3 fricDisp = (tan / tanLen) * fricCorr;

                        if (w0 > 0) positions[segIdx] -= fricDisp * (1.0f - t);
                        if (w1 > 0) positions[segIdx + 1] -= fricDisp * t;
                    }

                    // Zero out normal velocity to ensure inelastic behavior
                    float vNormal = math.dot(vPoint, normal);
                    float3 impulse = vNormal * normal;
                    if (w0 > 0) prevPositions[segIdx] += impulse * (1.0f - t);
                    if (w1 > 0) prevPositions[segIdx + 1] += impulse * t;
                }
            }
        }
    }

    [BurstCompile]
    struct BendingConstraintJob : IJob
    {
        public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float> inverseMasses;
        public float stiffness;
        public float dt;
        public bool IsClosedLoop;

        public void Execute()
        {
            int count = positions.Length;
            if (count < 3) return;

            // Map stiffness to compliance. 
            // For angular constraints, values might need different scaling.
            float clampedStiffness = math.clamp(stiffness, 1e-6f, 1f);
            float compliance = ((1.0f - clampedStiffness) / clampedStiffness) * 0.01f;
            float alphaTilde = compliance / (dt * dt);

            for (int i = 0; i < count - 2; i++)
            {
                ApplyAngularBending(i, i + 1, i + 2, alphaTilde);
            }

            if (IsClosedLoop)
            {
                ApplyAngularBending(count - 2, count - 1, 0, alphaTilde);
                ApplyAngularBending(count - 1, 0, 1, alphaTilde);
            }
        }

        private void ApplyAngularBending(int p0, int p1, int p2, float alphaTilde)
        {
            float w0 = inverseMasses[p0];
            float w1 = inverseMasses[p1];
            float w2 = inverseMasses[p2];
            
            if (w0 == 0 && w1 == 0 && w2 == 0) return;

            float3 v0 = positions[p0] - positions[p1];
            float3 v1 = positions[p2] - positions[p1];
            float L0Sq = math.lengthsq(v0);
            float L1Sq = math.lengthsq(v1);
            if (L0Sq < 1e-6f || L1Sq < 1e-6f) return;

            float3 n = math.cross(v0, v1);
            float nLen = math.length(n);

            float D = math.dot(v0, v1) / math.sqrt(L0Sq * L1Sq);
            D = math.clamp(D, -1f, 1f);

            if (nLen < 1e-6f)
            {
                if (D < -0.99f) return; // Already straight
                float3 any = math.abs(v0.x) > 0.1f ? new float3(0,1,0) : new float3(1,0,0);
                n = math.normalize(math.cross(v0, any));
                nLen = 1f;
            }

            float angle = math.acos(D);
            float C = angle - math.PI; // Goal is PI (straight line)

            float3 grad0 = math.cross(v0, n) / (L0Sq * nLen);
            float3 grad2 = math.cross(n, v1) / (L1Sq * nLen);
            float3 grad1 = -grad0 - grad2;

            float wSum = w0 * math.lengthsq(grad0) + w1 * math.lengthsq(grad1) + w2 * math.lengthsq(grad2);
            if (wSum < 1e-8f) return;

            float deltaLambda = -C / (wSum + alphaTilde);

            if (w0 > 0) positions[p0] += grad0 * deltaLambda * w0;
            if (w1 > 0) positions[p1] += grad1 * deltaLambda * w1;
            if (w2 > 0) positions[p2] += grad2 * deltaLambda * w2;
        }
    }

    #endregion
}
