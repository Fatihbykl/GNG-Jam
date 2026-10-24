using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tensio
{
    [AddComponentMenu("Tensio/Rendering/Rope Batch Manager")]
    [DefaultExecutionOrder(100)] // Run after RopeRenderer
    public class RopeBatchManager : MonoBehaviour
    {
        public static RopeBatchManager Instance { get; private set; }

#if UNITY_2019_3_OR_NEWER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            Instance = null;
        }
#endif

        [SerializeField] private bool enableBatching = true;

        private readonly List<RopeRenderer> _allRopes = new List<RopeRenderer>();
        private readonly Dictionary<Material, BatchGroup> _batches = new Dictionary<Material, BatchGroup>();

        private void Awake()
        {
            if (Instance != null && Instance != this) Destroy(this);
            else Instance = this;
        }

        private void OnDestroy()
        {
            foreach (var batch in _batches.Values)
            {
                batch.Dispose();
            }
            _batches.Clear();
            if (Instance == this) Instance = null;
        }

        public void Register(RopeRenderer rope)
        {
            if (!_allRopes.Contains(rope)) _allRopes.Add(rope);
        }

        public void Unregister(RopeRenderer rope)
        {
            _allRopes.Remove(rope);
        }

        private void LateUpdate()
        {
            if (!enableBatching) return;

            // 1. Clear current batches
            foreach (var batch in _batches.Values)
            {
                batch.Ropes.Clear();
            }

            // 2. Distribute ropes to batches
            for (int i = 0; i < _allRopes.Count; i++)
            {
                var rope = _allRopes[i];
                if (rope == null || !rope.isActiveAndEnabled || !rope.HasData) continue;

                Material mat = rope.GetComponent<MeshRenderer>().sharedMaterial;
                if (mat == null) continue;

                if (!_batches.TryGetValue(mat, out var group))
                {
                    group = new BatchGroup(mat);
                    _batches.Add(mat, group);
                }
                group.Ropes.Add(rope);
            }

            // 3. Process each batch
            foreach (var batch in _batches.Values)
            {
                ProcessBatch(batch);
            }
        }

        private void ProcessBatch(BatchGroup group)
        {
            if (group.Ropes.Count == 0) return;

            // 1. Calculate totals
            int totalVerts = 0;
            int totalIndices = 0;

            foreach (var rope in group.Ropes)
            {
                totalVerts += rope.VertexCount;
                totalIndices += rope.IndexCount;
            }

            if (totalVerts == 0) return;

            // 2. Resize buffers if needed
            if (!group.Vertices.IsCreated || group.Vertices.Length < totalVerts)
            {
                if (group.Vertices.IsCreated) group.Vertices.Dispose();
                group.Vertices = new NativeArray<RopeRenderer.RopeVertex>(Mathf.NextPowerOfTwo(totalVerts), Allocator.Persistent);
            }
            if (!group.Indices.IsCreated || group.Indices.Length < totalIndices)
            {
                if (group.Indices.IsCreated) group.Indices.Dispose();
                group.Indices = new NativeArray<int>(Mathf.NextPowerOfTwo(totalIndices), Allocator.Persistent);
            }

            // 3. Schedule Merge Jobs
            NativeArray<JobHandle> copyHandles = new NativeArray<JobHandle>(group.Ropes.Count, Allocator.Temp);
            int currentV = 0;
            int currentI = 0;
            int jobIdx = 0;

            foreach (var rope in group.Ropes)
            {
                JobHandle ropeDependency = rope.GetGenerationJobHandle();

                var copyVerts = new CopyVerticesJob
                {
                    Source = rope.GetVertices(),
                    Destination = group.Vertices,
                    DstOffset = currentV
                };
                JobHandle h1 = copyVerts.Schedule(rope.VertexCount, 64, ropeDependency);

                var copyIndices = new CopyIndicesJob
                {
                    Source = rope.GetIndices(),
                    Destination = group.Indices,
                    DstOffset = currentI,
                    VertexOffset = currentV
                };
                JobHandle h2 = copyIndices.Schedule(rope.IndexCount, 64, ropeDependency);

                copyHandles[jobIdx++] = JobHandle.CombineDependencies(h1, h2);

                currentV += rope.VertexCount;
                currentI += rope.IndexCount;
            }

            JobHandle.CompleteAll(copyHandles);
            copyHandles.Dispose();

            // 4. Update Mesh
            group.Mesh.SetVertexBufferParams(totalVerts,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
            
            group.Mesh.indexFormat = IndexFormat.UInt32;
            group.Mesh.SetIndexBufferParams(totalIndices, IndexFormat.UInt32); 
            
            group.Mesh.SetVertexBufferData(group.Vertices, 0, 0, totalVerts);
            group.Mesh.SetIndexBufferData(group.Indices, 0, 0, totalIndices);
            
            group.Mesh.SetSubMesh(0, new SubMeshDescriptor(0, totalIndices), UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
            
            group.Mesh.RecalculateBounds();

            // 5. Draw
            Graphics.DrawMesh(group.Mesh, Matrix4x4.identity, group.Material, 0);
        }

        private class BatchGroup
        {
            public Material Material;
            public List<RopeRenderer> Ropes = new List<RopeRenderer>();
            public Mesh Mesh;
            public NativeArray<RopeRenderer.RopeVertex> Vertices;
            public NativeArray<int> Indices;

            public BatchGroup(Material material)
            {
                Material = material;
                Mesh = new Mesh { name = $"BatchMesh_{material.name}" };
                Mesh.MarkDynamic();
                Mesh.indexFormat = IndexFormat.UInt32;
            }

            public void Dispose()
            {
                if (Vertices.IsCreated) Vertices.Dispose();
                if (Indices.IsCreated) Indices.Dispose();
                if (Mesh != null) Object.Destroy(Mesh);
            }
        }

        [BurstCompile]
        struct CopyVerticesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RopeRenderer.RopeVertex> Source;
            [NativeDisableContainerSafetyRestriction] [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<RopeRenderer.RopeVertex> Destination;
            public int DstOffset;

            public void Execute(int i)
            {
                Destination[DstOffset + i] = Source[i];
            }
        }

        [BurstCompile]
        struct CopyIndicesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ushort> Source;
            [NativeDisableContainerSafetyRestriction] [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<int> Destination;
            public int DstOffset;
            public int VertexOffset;

            public void Execute(int i)
            {
                Destination[DstOffset + i] = (int)Source[i] + VertexOffset;
            }
        }
    }
}