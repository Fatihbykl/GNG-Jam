using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Tensio
{
    [AddComponentMenu("Tensio/Interaction/Rope Interaction Manager")]
    [DefaultExecutionOrder(100)]
    public class RopeInteractionManager : MonoBehaviour
    {
        public static RopeInteractionManager Instance { get; private set; }

#if UNITY_2019_3_OR_NEWER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            Instance = null;
        }
#endif

        private List<RopeController> _ropes = new List<RopeController>();
        private NativeParallelMultiHashMap<int, RopeSolver.GlobalSpatialEntry> _globalSpatialMap;
        
        // Configuration
        [Tooltip("Number of sub-steps for inter-rope collision. Overrides individual rope settings if enabled.")]
        public int SubSteps = 10;

        [Tooltip("Cell size for the spatial hash. Should be larger than the thickest rope's diameter.")]
        public float CellSize = 0.2f;

        public bool VisualizeVirtualParticles = false;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);

            _globalSpatialMap = new NativeParallelMultiHashMap<int, RopeSolver.GlobalSpatialEntry>(10000, Allocator.Persistent);
        }

        private void OnDestroy()
        {
            GlobalMapDependency.Complete();
            if (_globalSpatialMap.IsCreated) _globalSpatialMap.Dispose();
            if (Instance == this) Instance = null;
        }

        private void OnDrawGizmos()
        {
            if (!VisualizeVirtualParticles || !_globalSpatialMap.IsCreated || _globalSpatialMap.IsEmpty) return;

            Gizmos.color = Color.cyan;
            var values = _globalSpatialMap.GetValueArray(Allocator.Temp);
            for (int i = 0; i < values.Length; i++)
            {
                // Draw Capsule/Segment
                Gizmos.DrawLine(values[i].Position, values[i].EndPosition);
                Gizmos.DrawWireSphere(values[i].Position, values[i].Radius * 0.5f); // Start
                Gizmos.DrawWireSphere(values[i].EndPosition, values[i].Radius * 0.5f); // End
            }
            values.Dispose();
        }

        public void Register(RopeController rope)
        {
            if (!_ropes.Contains(rope)) _ropes.Add(rope);
        }

        public void Unregister(RopeController rope)
        {
            _ropes.Remove(rope);
        }

        public JobHandle GlobalMapDependency { get; private set; }

        private void FixedUpdate()
        {
            if (_ropes.Count == 0) return;

            // 1. Prepare & Allocation Check
            int totalParticles = 0;
            float maxRadius = 0.05f;
            float maxSegmentLength = 0.1f;

            foreach (var rope in _ropes)
            {
                if (rope != null && rope.Solver != null && rope.Solver.IsInitialized)
                {
                    totalParticles += rope.Solver.Positions.Length;
                    if (rope.PhysicsSettings.ParticleRadius > maxRadius) maxRadius = rope.PhysicsSettings.ParticleRadius;
                    
                    float len = Mathf.Max(rope.InitSettings.Length, rope.Solver.TotalLength);
                    float segLen = len / Mathf.Max(1, rope.InitSettings.ParticleCount - 1);
                    if (segLen > maxSegmentLength) maxSegmentLength = segLen;
                }
            }

            if (totalParticles == 0) return;

            // Dynamically adjust CellSize
            CellSize = Mathf.Max(maxRadius * 2.2f, maxSegmentLength * 1.5f);
            if (CellSize < 0.25f) CellSize = 0.25f;

            int capacityNeeded = totalParticles * 64; // Max 64 (4x4x4) cells per swept AABB segment
            if (_globalSpatialMap.Capacity < capacityNeeded)
            {
                _globalSpatialMap.Dispose();
                _globalSpatialMap = new NativeParallelMultiHashMap<int, RopeSolver.GlobalSpatialEntry>(capacityNeeded, Allocator.Persistent);
            }

            // 2. Main Synchronized Loop
            float dt = Time.fixedDeltaTime;
            float subStepDt = dt / SubSteps;
            float time = Time.time;

            // We need to complete any previous frame jobs before starting
            foreach (var rope in _ropes) rope.Solver.CompleteJobs();

            JobHandle dependency = default;

            for (int s = 0; s < SubSteps; s++)
            {
                _globalSpatialMap.Clear();

                // Phase A: Populate Global Map with current (or newly integrated) positions
                foreach (var rope in _ropes)
                {
                    if (rope == null || rope.Solver == null || !rope.Solver.IsInitialized) continue;
                    if (!rope.PhysicsSettings.SupportInterRopeCollision) continue;

                    var populateJob = new PopulateGlobalMapJob
                    {
                        positions = rope.Solver.Positions,
                        prevPositions = rope.Solver.PrevPositions,
                        inverseMasses = rope.Solver.InverseMasses,
                        globalSpatialMap = _globalSpatialMap.AsParallelWriter(),
                        cellSize = CellSize,
                        radius = rope.PhysicsSettings.ParticleRadius,
                        ropeId = rope.RopeId
                    };
                    dependency = populateJob.Schedule(rope.Solver.Positions.Length, 64, dependency);
                }
                
                // We MUST complete population before resolving, as it's a MultiHashMap shared across all ropes
                dependency.Complete(); 
                dependency = default;

                // Phase B: Each rope performs its local steps + inter-rope collision
                // We pass subSteps=1 because we are manually orchestrating the loop here
                foreach (var rope in _ropes)
                {
                    if (rope == null || rope.Solver == null || !rope.Solver.IsInitialized) continue;

                    float3 windVel = (rope.AeroSettings.WindSpeed > 0)
                        ? ((Vector3)rope.AeroSettings.WindDirection).normalized * rope.AeroSettings.WindSpeed
                        : Vector3.zero;

                    rope.Solver.Step(
                        dt: subStepDt, 
                        subSteps: 1, // Single step controlled by manager
                        gravity: Physics.gravity,
                        compliance: rope.PhysicsSettings.Compliance,
                        friction: rope.PhysicsSettings.Friction,
                        particleRadius: rope.PhysicsSettings.ParticleRadius,
                        linearDamping: rope.PhysicsSettings.LinearDamping,
                        bendingStiffness: rope.PhysicsSettings.BendingStiffness,
                        windVelocity: windVel,
                        turbulenceStrength: rope.AeroSettings.TurbulenceStrength,
                        turbulenceFrequency: rope.AeroSettings.TurbulenceFrequency,
                        airDrag: rope.AeroSettings.AirDrag,
                        time: time + (s * subStepDt),
                        globalSpatialMap: _globalSpatialMap,
                        globalCellSize: CellSize,
                        interRopeStiffness: rope.PhysicsSettings.InterRopeStiffness,
                        collisionLayer: rope.PhysicsSettings.CollisionMask,
                        IsClosedLoop: rope.InitSettings.IsClosedLoop,
                        maxVelocity: rope.PhysicsSettings.MaxVelocity,
                        massScale: rope.PhysicsSettings.MassScale,
                        externalDependency: default
                    );
                }

                // Finalize sub-step jobs for all ropes to synchronize
                foreach (var rope in _ropes) dependency = JobHandle.CombineDependencies(dependency, rope.GetSolverHandle());
                dependency.Complete(); 
                dependency = default;
            }

            // 3. Finalize all ropes (Breaking logic, Stats, etc.)
            foreach (var rope in _ropes)
            {
                if (rope != null) rope.FinalizeFrame();
            }

            GlobalMapDependency = dependency;
        }

        public NativeParallelMultiHashMap<int, RopeSolver.GlobalSpatialEntry> GetGlobalMap()
        {
            return _globalSpatialMap;
        }
    }
}