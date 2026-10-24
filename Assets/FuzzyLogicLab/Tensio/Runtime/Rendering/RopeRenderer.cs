using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Burst;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;

namespace Tensio
{
    public enum RopeGeometryType { Single, MultiStrand }

    [DefaultExecutionOrder(250)]
    [AddComponentMenu("Tensio/Rendering/Rope Renderer")]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [RequireComponent(typeof(RopeController))]
    public class RopeRenderer : MonoBehaviour
    {
        [Header("Mesh Settings")]
        [SerializeField] private RopeGeometryType geometryType = RopeGeometryType.Single;
        [Range(0.01f, 1f)] [SerializeField] private float radius = 0.1f;
        [Range(3, 32)] [SerializeField] private int tubeSegments = 8;
        [Range(1, 20)] [SerializeField] private int meshResolution = 4;
        
        [Header("Multi-Strand Settings")]
        [Range(1, 8)] [SerializeField] private int strandCount = 3;
        [SerializeField] private float strandOffset = 0.05f;
        [SerializeField] private float strandRadius = 0.04f;

        [Header("UV Settings")]
        [SerializeField] private float textureTiling = 1.0f;
        [SerializeField] private float uvOffset = 0.0f;
        [SerializeField] private bool closeEnds = true;
        [SerializeField] private bool useBatching = true;
        
        [Header("Rendering")]
        [SerializeField] private Material ropeMaterial;

        [Header("Twist Settings")]
        [Tooltip("Number of full 360-degree twists per meter (or world unit) of rope length.")]
        [SerializeField] private float turnsPerMeter = 0.0f;
        [Tooltip("Base twist offset in full turns.")]
        [SerializeField] private float twistOffset = 0.0f;
        [Tooltip("Animation speed of the twist in turns per second.")]
        [SerializeField] private float twistSpeed = 0.0f;
        [Tooltip("If enabled, the twist density will decrease as the rope stretches.")]
        [SerializeField] private bool stretchReactiveTwist = false;
        [Tooltip("The stretch ratio at stretch reactive twist reach their peak. Used when BreakOnStretch is disabled.")]
        [SerializeField] private float VisualMaxStretchRatio = 1.5f;
        [Tooltip("The minimum multiplier for TurnsPerMeter at maximum stretch.")]
        [Range(0f, 1f)] [SerializeField] private float minTwistScale = 0.5f;
        
        public float TurnsPerMeter { get => turnsPerMeter; set => turnsPerMeter = value; }
        public float TwistOffset { get => twistOffset; set => twistOffset = value; }
        public float TwistSpeed { get => twistSpeed; set => twistSpeed = value; }
        public bool StretchReactiveTwist { get => stretchReactiveTwist; set => stretchReactiveTwist = value; }
        public float MinTwistScale { get => minTwistScale; set => minTwistScale = value; }

        public float UVOffset { get => uvOffset; set => uvOffset = value; }
        public float Radius { get => radius; set => radius = value; }
        public float TextureTiling { get => textureTiling; set => textureTiling = value; }
        

        private RopeController _controller;
        private Mesh _mesh;
        private MeshRenderer _meshRenderer;
        private RopeVertexData _data; 
        private JobHandle _generationJobHandle;
        private bool _materialWarningLogged = false;

        private float _animatedTwist = 0f;

        private MaterialPropertyBlock _propBlock;
        private Color _initialColor = Color.white;
        private int _colorPropId;
        
        public Material Material 
        { 
            get => ropeMaterial; 
            set 
            { 
                ropeMaterial = value;
                if (_meshRenderer != null) _meshRenderer.sharedMaterial = ropeMaterial;
                if (value != null) _materialWarningLogged = false;
            } 
        }

        private void OnValidate()
        {
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
            if (_meshRenderer != null && ropeMaterial != null && _meshRenderer.sharedMaterial != ropeMaterial)
            {
                _meshRenderer.sharedMaterial = ropeMaterial;
            }
            if (ropeMaterial != null || (_meshRenderer != null && _meshRenderer.sharedMaterial != null))
            {
                _materialWarningLogged = false;
            }
        }

        // Helper class to manage NativeArrays
        public class RopeVertexData
        {
            public NativeArray<RopeVertex> Vertices;
            public NativeArray<ushort> Indices;
            public NativeArray<RopeFrame> Frames;
            public float3 LastNormal = new float3(0, 1, 0);
            public bool IsFirstUpdate = true;
            
            public void Dispose()
            {
                if (Vertices.IsCreated) Vertices.Dispose();
                if (Indices.IsCreated) Indices.Dispose();
                if (Frames.IsCreated) Frames.Dispose();
            }
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct RopeVertex
        {
            public float3 position;
            public float3 normal;
            public float4 tangent;
            public float2 uv;
        }

        public struct RopeFrame
        {
            public float3 position;
            public float3 tangent;
            public float3 normal;
            public float3 binormal;
            public float vCoord; 
        }

        // Batching Interface
        public bool HasData => _data != null && _data.Vertices.IsCreated;
        public int VertexCount => _data.Vertices.Length;
        public int IndexCount => _data.Indices.Length;
        public NativeArray<RopeVertex> GetVertices() => _data.Vertices;
        public NativeArray<ushort> GetIndices() => _data.Indices;
        public JobHandle GetGenerationJobHandle() => _generationJobHandle;

        public Mesh BakeMesh()
        {
            if (!HasData) return null;
            
            _generationJobHandle.Complete();

            Mesh m = new Mesh();
            m.name = "BakedRopeMesh";
            
            int vCount = VertexCount;
            int iCount = IndexCount;
            
            m.SetVertexBufferParams(vCount, 
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
            m.SetIndexBufferParams(iCount, IndexFormat.UInt16);
            
            m.SetVertexBufferData(_data.Vertices, 0, 0, vCount);
            m.SetIndexBufferData(_data.Indices, 0, 0, iCount);
            m.SetSubMesh(0, new SubMeshDescriptor(0, iCount), UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
            
            m.RecalculateBounds();

            if (useBatching)
            {
                Vector3[] verts = m.vertices;
                Vector3[] normals = m.normals;
                Vector4[] tangents = m.tangents;
                Matrix4x4 w2l = transform.worldToLocalMatrix;
                for (int i = 0; i < verts.Length; i++)
                {
                    verts[i] = w2l.MultiplyPoint3x4(verts[i]);
                    normals[i] = w2l.MultiplyVector(normals[i]);
                    Vector3 t = w2l.MultiplyVector(new Vector3(tangents[i].x, tangents[i].y, tangents[i].z));
                    tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w);
                }
                m.vertices = verts;
                m.normals = normals;
                m.tangents = tangents;
                m.RecalculateBounds();
            }

            return m;
        }

        private void Awake()
        {
            _controller = GetComponent<RopeController>();
            _meshRenderer = GetComponent<MeshRenderer>();
            
            if (_meshRenderer != null)
            {
                _meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            }
            
            // Sync Material
            if (ropeMaterial == null && _meshRenderer != null && _meshRenderer.sharedMaterial != null)
                ropeMaterial = _meshRenderer.sharedMaterial;
            else if (ropeMaterial != null && _meshRenderer != null && _meshRenderer.sharedMaterial != ropeMaterial)
                _meshRenderer.sharedMaterial = ropeMaterial;

            if (ropeMaterial == null && (_meshRenderer == null || _meshRenderer.sharedMaterial == null))
            {
                Debug.LogError($"[Tensio] Missing Material on RopeRenderer ({gameObject.name})! Please assign a Material in the RopeRenderer component.", this);
                _materialWarningLogged = true;
            }

            _mesh = new Mesh { name = "ProceduralRopeMesh" };
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _mesh.MarkDynamic();
            _data = new RopeVertexData();

            _propBlock = new MaterialPropertyBlock();

            _colorPropId = Shader.PropertyToID("_BaseColor");
            if (_meshRenderer.sharedMaterial != null)
            {
                if (!_meshRenderer.sharedMaterial.HasProperty(_colorPropId))
                    _colorPropId = Shader.PropertyToID("_Color");
                
                if (_meshRenderer.sharedMaterial.HasProperty(_colorPropId))
                    _initialColor = _meshRenderer.sharedMaterial.GetColor(_colorPropId);
            }

            if (useBatching)
            {
                if (RopeBatchManager.Instance == null)
                {
                    var go = new GameObject("RopeBatchManager");
                    go.AddComponent<RopeBatchManager>();
                }
                RopeBatchManager.Instance.Register(this);
            }
            
            // --- Pre-warm First Frame Mesh ---
            bool canPrewarm = _controller != null && _controller.EditorPreviewPositions != null && _controller.EditorPreviewPositions.Length == _controller.InitSettings.ParticleCount;
#if TENSIO_SPLINES_SUPPORT
            if (_controller != null && _controller.InitMode == RopeController.RopeInitMode.Spline)
            {
                canPrewarm = false;
            }
#endif
            if (canPrewarm)
            {
                int pCount = _controller.InitSettings.ParticleCount;
                bool isClosedLoop = _controller.InitSettings.IsClosedLoop;
                int segmentCount = isClosedLoop ? pCount : pCount - 1;
                int totalRings = segmentCount * math.max(1, meshResolution) + 1;
                
                UpdateMeshBuffers(totalRings, isClosedLoop);

                float3 spawnOffset = Application.isPlaying ? ((float3)transform.position - (float3)_controller.EditorPreviewPositions[0]) : float3.zero;

                var posNative = new NativeArray<float3>(pCount, Allocator.TempJob);
                for (int i = 0; i < pCount; i++) posNative[i] = (float3)_controller.EditorPreviewPositions[i] + spawnOffset;

            var frameJob = new SplineAndFrameJob
            {
                positions = posNative,
                frames = _data.Frames,
                resolution = math.max(1, meshResolution),
                worldToLocal = useBatching ? Matrix4x4.identity : transform.worldToLocalMatrix,
                textureTiling = textureTiling,
                uvOffset = uvOffset,
                isClosedLoop = isClosedLoop,
                twistRadiansPerUnit = turnsPerMeter * 2f * Mathf.PI
            };
                JobHandle fh = frameJob.Schedule();

                var meshJob = new GenerateMeshVerticesJob
                {
                    frames = _data.Frames,
                    vertices = _data.Vertices,
                    geometryType = geometryType,
                    radius = radius,
                    segments = tubeSegments,
                    strandCount = strandCount,
                    strandOffset = strandOffset,
                    strandRadius = strandRadius,
                    twistRadiansPerUnit = turnsPerMeter * 2f * Mathf.PI
                };
                JobHandle mh = meshJob.Schedule(totalRings, 64, fh);
                
                if (closeEnds && !isClosedLoop)
                {
                    int sCount = (geometryType == RopeGeometryType.MultiStrand) ? strandCount : 1;
                    var capsJob = new GenerateCapsJob
                    {
                        frames = _data.Frames,
                        vertices = _data.Vertices,
                        geometryType = geometryType,
                        strandCount = strandCount,
                        strandOffset = strandOffset,
                        twistRadiansPerUnit = turnsPerMeter * 2f * Mathf.PI,
                        startCapIndex = totalRings * sCount * (tubeSegments + 1),
                        endCapIndex = totalRings * sCount * (tubeSegments + 1) + sCount
                    };
                    mh = capsJob.Schedule(mh);
                }

                mh.Complete();
                
                if (_data.Frames.Length > 0)
                {
                    _data.LastNormal = _data.Frames[0].normal;
                    _data.IsFirstUpdate = false;
                }

                int vCount = VertexCount;
                int iCount = IndexCount;

                // Apply to mesh immediately
                _mesh.SetVertexBufferParams(vCount, 
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
                _mesh.SetIndexBufferParams(iCount, IndexFormat.UInt16);
                
                _mesh.SetVertexBufferData(_data.Vertices, 0, 0, vCount);
                _mesh.SetIndexBufferData(_data.Indices, 0, 0, iCount);
                _mesh.SetSubMesh(0, new SubMeshDescriptor(0, iCount), UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
                _mesh.RecalculateBounds();

                //posNative.Dispose();
            }
        }

        private void OnDestroy()
        {
            _generationJobHandle.Complete();
            if (useBatching && RopeBatchManager.Instance != null)
                RopeBatchManager.Instance.Unregister(this);
            _data?.Dispose();
        }

        private void OnEnable()
        {
            if (useBatching && RopeBatchManager.Instance != null)
                RopeBatchManager.Instance.Register(this);
        }

        private void OnDisable()
        {
            if (useBatching && RopeBatchManager.Instance != null)
                RopeBatchManager.Instance.Unregister(this);
        }

        private void LateUpdate()
        {
            Material activeMat = ropeMaterial != null ? ropeMaterial : (_meshRenderer != null ? _meshRenderer.sharedMaterial : null);
            if (activeMat == null)
            {
                if (!_materialWarningLogged)
                {
                    Debug.LogError($"[Tensio] Missing Material on RopeRenderer ({gameObject.name})! Please assign a Material in the RopeRenderer component.", this);
                    _materialWarningLogged = true;
                }
                _generationJobHandle.Complete();
                return;
            }
            _materialWarningLogged = false;

            if (useBatching) UpdateInternalState();

            if (_controller == null || _controller.Solver == null || !_controller.Solver.IsInitialized) return;

            NativeArray<float3> positions = _controller.Solver.Positions;
            bool isClosedLoop = _controller.InitSettings.IsClosedLoop;
            
            // If looped, we have 'count' segments. If open, 'count-1'.
            int segmentCount = isClosedLoop ? positions.Length : positions.Length - 1;
            int totalRings = segmentCount * math.max(1, meshResolution) + 1;
            
            UpdateMeshBuffers(totalRings, isClosedLoop);

            // Get Interpolated + Pin-Snapped Visual Positions
            var visualPositions = new NativeArray<float3>(positions.Length, Allocator.TempJob);
            _controller.GetVisualPositions(visualPositions);

            // --- Visual Feedback Logic ---
            float currentRadius = radius;
            float currentTurnsPerMeter = turnsPerMeter;
            _animatedTwist += twistSpeed * Time.deltaTime;
            
            float maxVisualStretch = _controller.Settings.BreakOnStretch ? _controller.Settings.MaxStretchRatio : VisualMaxStretchRatio;
            float t = Mathf.InverseLerp(1.0f, maxVisualStretch, _controller.CurrentStressRatio);
            
            // Stretch-Reactive Twist
            if (stretchReactiveTwist)
            {
                currentTurnsPerMeter = turnsPerMeter * Mathf.Lerp(1.0f, minTwistScale, t);
            }

            if (_controller.Settings.VisualizeStretch)
            {
                // Thinning
                currentRadius = Mathf.Lerp(radius, radius * _controller.Settings.MaxThinningScale, t);
                
                // Color
                _meshRenderer.GetPropertyBlock(_propBlock);
                _propBlock.SetColor(_colorPropId, Color.Lerp(_initialColor, _controller.Settings.StretchColor, t));
                _meshRenderer.SetPropertyBlock(_propBlock);
            }
            else
            {
                // Reset Color
                _meshRenderer.GetPropertyBlock(_propBlock);
                _propBlock.SetColor(_colorPropId, _initialColor);
                _meshRenderer.SetPropertyBlock(_propBlock);
            }

            var frameJob = new SplineAndFrameJob
            {
                positions = visualPositions,
                frames = _data.Frames,
                resolution = math.max(1, meshResolution),
                worldToLocal = useBatching ? Matrix4x4.identity : transform.worldToLocalMatrix,
                textureTiling = textureTiling,
                uvOffset = uvOffset,
                isClosedLoop = isClosedLoop,
                twistRadiansPerUnit = currentTurnsPerMeter * 2f * Mathf.PI,
                baseTwistRadians = (twistOffset + _animatedTwist) * 2f * Mathf.PI
            };
            JobHandle fh = frameJob.Schedule();

            var meshJob = new GenerateMeshVerticesJob
            {
                frames = _data.Frames,
                vertices = _data.Vertices,
                geometryType = geometryType,
                radius = currentRadius,
                segments = tubeSegments,
                strandCount = strandCount,
                strandOffset = strandOffset,
                strandRadius = strandRadius,
                twistRadiansPerUnit = currentTurnsPerMeter * 2f * Mathf.PI
            };
            _generationJobHandle = meshJob.Schedule(totalRings, 64, fh);
            
            if (closeEnds && !isClosedLoop)
            {
                int sCount = (geometryType == RopeGeometryType.MultiStrand) ? strandCount : 1;
                var capsJob = new GenerateCapsJob
                {
                    frames = _data.Frames,
                    vertices = _data.Vertices,
                    geometryType = geometryType,
                    strandCount = strandCount,
                    strandOffset = strandOffset,
                    twistRadiansPerUnit = currentTurnsPerMeter * 2f * Mathf.PI,
                    startCapIndex = totalRings * sCount * (tubeSegments + 1),
                    endCapIndex = totalRings * sCount * (tubeSegments + 1) + sCount
                };
                _generationJobHandle = capsJob.Schedule(_generationJobHandle);
            }
            
            if (!useBatching)
            {
                _generationJobHandle.Complete();
                
                if (_data.Frames.Length > 0)
                {
                    _data.LastNormal = _data.Frames[0].normal;
                    _data.IsFirstUpdate = false;
                }

                ApplyMesh();
                if (_meshRenderer) _meshRenderer.enabled = true;
            }
            else
            {
                
                if (_data.Frames.IsCreated && _data.Frames.Length > 0 && !_data.IsFirstUpdate) 
                {
                    
                }
                
                // Initial update flag handling
                if (_data.Frames.Length > 0)
                {
                     
                }
                
                if (_meshRenderer) _meshRenderer.enabled = false;
            }
        }
        
        public void UpdateInternalState()
        {
             _generationJobHandle.Complete();
             if (_data != null && _data.Frames.IsCreated && _data.Frames.Length > 0)
             {
                 _data.LastNormal = _data.Frames[0].normal;
                 _data.IsFirstUpdate = false;
             }
        }

        private void UpdateMeshBuffers(int totalRings, bool isClosedLoop)
        {
            int sCount = (geometryType == RopeGeometryType.MultiStrand) ? strandCount : 1;
            int capVCount = (closeEnds && !isClosedLoop) ? 2 * sCount : 0;
            int capICount = (closeEnds && !isClosedLoop) ? (tubeSegments * 6 * sCount) : 0;

            int vCount = totalRings * sCount * (tubeSegments + 1) + capVCount;
            int iCount = (totalRings - 1) * sCount * tubeSegments * 6 + capICount;
            
            if (!_data.Vertices.IsCreated || _data.Vertices.Length != vCount)
            {
                _data.Dispose();
                _data.Vertices = new NativeArray<RopeVertex>(vCount, Allocator.Persistent);
                _data.Indices = new NativeArray<ushort>(iCount, Allocator.Persistent);
                _data.Frames = new NativeArray<RopeFrame>(totalRings, Allocator.Persistent, NativeArrayOptions.ClearMemory);

                _mesh.Clear();
                _mesh.SetVertexBufferParams(vCount, 
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
                _mesh.SetIndexBufferParams(iCount, IndexFormat.UInt16);
                
                GenerateIndices(totalRings, isClosedLoop);
                _mesh.SetIndexBufferData(_data.Indices, 0, 0, iCount);
                _mesh.SetSubMesh(0, new SubMeshDescriptor(0, iCount), UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
            }
        }

        private void GenerateIndices(int totalRings, bool isClosedLoop)
        {
            int i = 0;
            int sCount = (geometryType == RopeGeometryType.MultiStrand) ? strandCount : 1;
            int vertsPerRing = sCount * (tubeSegments + 1);

            for (int r = 0; r < totalRings - 1; r++)
            {
                for (int sIdx = 0; sIdx < sCount; sIdx++)
                {
                    int ringOffset = r * vertsPerRing + sIdx * (tubeSegments + 1);
                    int nextRingOffset = (r + 1) * vertsPerRing + sIdx * (tubeSegments + 1);
                    
                    for (int s = 0; s < tubeSegments; s++)
                    {
                        int c = ringOffset + s;
                        int n = nextRingOffset + s;
                        _data.Indices[i++] = (ushort)(c); _data.Indices[i++] = (ushort)(n); _data.Indices[i++] = (ushort)(c + 1);
                        _data.Indices[i++] = (ushort)(n); _data.Indices[i++] = (ushort)(n + 1); _data.Indices[i++] = (ushort)(c + 1);
                    }
                }
            }

            if (closeEnds && !isClosedLoop)
            {
                int startCapBase = totalRings * sCount * (tubeSegments + 1);
                int endCapBase = startCapBase + sCount;

                for (int k = 0; k < sCount; k++)
                {
                    int startCapIndex = startCapBase + k;
                    int endCapIndex = endCapBase + k;
                    int strandRingOffset = k * (tubeSegments + 1);

                    // Start Cap
                    for (int s = 0; s < tubeSegments; s++)
                    {
                        _data.Indices[i++] = (ushort)startCapIndex;
                        _data.Indices[i++] = (ushort)(strandRingOffset + s + 1);
                        _data.Indices[i++] = (ushort)(strandRingOffset + s);
                    }

                    // End Cap
                    int lastRingStart = (totalRings - 1) * vertsPerRing + strandRingOffset;
                    for (int s = 0; s < tubeSegments; s++)
                    {
                        _data.Indices[i++] = (ushort)endCapIndex;
                        _data.Indices[i++] = (ushort)(lastRingStart + s);
                        _data.Indices[i++] = (ushort)(lastRingStart + s + 1);
                    }
                }
            }
        }

        private void ApplyMesh()
        {
            _mesh.SetVertexBufferData(_data.Vertices, 0, 0, _data.Vertices.Length);
            _mesh.RecalculateBounds();
        }

        [BurstCompile]
        struct SplineAndFrameJob : IJob
        {
            [ReadOnly] [DeallocateOnJobCompletion] public NativeArray<float3> positions;
            public NativeArray<RopeFrame> frames;
            
            public int resolution;
            public float4x4 worldToLocal;
            public float textureTiling;
            public float uvOffset;
            public bool isClosedLoop;
            public float twistRadiansPerUnit;
            public float baseTwistRadians;

            public void Execute()
            {
                int particleCount = positions.Length;
                if (particleCount < 2) return;

                int frameIndex = 0;
                float accumulatedLen = 0f;
                float3 prevPos = float3.zero;

                float3 p1 = math.transform(worldToLocal, positions[0]);
                float3 p2 = math.transform(worldToLocal, positions[1]);
                
                float3 startTangent = new float3(0, -1, 0);
                
                if (isClosedLoop)
                {
                    float3 p0 = math.transform(worldToLocal, positions[particleCount - 1]);
                    float3 diff = p1 - p0;
                    float lenSq = math.lengthsq(diff);
                    if (lenSq > 1e-6f) startTangent = diff * math.rsqrt(lenSq);
                }
                else
                {
                    float3 diff = p2 - p1;
                    float lenSq = math.lengthsq(diff);
                    if (lenSq > 1e-6f)
                    {
                        startTangent = diff * math.rsqrt(lenSq);
                    }
                    else
                    {
                        for (int k = 2; k < particleCount; k++)
                        {
                            float3 pk = math.transform(worldToLocal, positions[k]);
                            float3 diffK = pk - p1;
                            float lenSqK = math.lengthsq(diffK);
                            if (lenSqK > 1e-6f)
                            {
                                startTangent = diffK * math.rsqrt(lenSqK);
                                break;
                            }
                        }
                    }
                }
                
                float3 currentUntwistedNormal;

                // Read previous frame data before overwriting
                RopeFrame prevFrame = frames[0];
                float3 prevStartNormal = prevFrame.normal;
                float3 prevStartTangent = prevFrame.tangent;

                // Check if valid (initialized)
                if (math.lengthsq(prevStartNormal) > 0.001f && math.lengthsq(prevStartTangent) > 0.001f)
                {
                    quaternion rot = FromToRotation(prevStartTangent, startTangent);
                    currentUntwistedNormal = math.rotate(rot, prevStartNormal);
                }
                else
                {
                    // First run or invalid: Use global up
                    currentUntwistedNormal = CalculateNormalFromGlobal(startTangent);
                }

                // Orthogonalize
                float3 rawBinormal = math.cross(startTangent, currentUntwistedNormal);
                float bLenSq = math.lengthsq(rawBinormal);
                float3 currentBinormal = (bLenSq > 1e-6f) ? (rawBinormal * math.rsqrt(bLenSq)) : new float3(1, 0, 0);
                currentUntwistedNormal = math.cross(currentBinormal, startTangent);
                float uLenSq = math.lengthsq(currentUntwistedNormal);
                if (uLenSq > 1e-6f) currentUntwistedNormal *= math.rsqrt(uLenSq);
                else currentUntwistedNormal = new float3(0, 1, 0);

                float3 startPos = p1;

                frames[0] = new RopeFrame { position = startPos, tangent = startTangent, normal = currentUntwistedNormal, binormal = currentBinormal, vCoord = uvOffset };
                prevPos = startPos;
                frameIndex++;

                float3 prevUntwistedNormal = currentUntwistedNormal;
                float3 prevUntwistedTangent = startTangent;

                int segments = isClosedLoop ? particleCount : particleCount - 1;

                for (int i = 0; i < segments; i++)
                {
                    int i0 = isClosedLoop ? (i + particleCount - 1) % particleCount : math.max(0, i - 1);
                    int i1 = i;
                    int i2 = (i + 1) % particleCount;
                    int i3 = (i + 2) % particleCount;
                    
                    if (!isClosedLoop)
                    {
                        if (i == 0) i0 = 0;
                        i2 = math.min(i + 1, particleCount - 1);
                        i3 = math.min(i + 2, particleCount - 1);
                    }

                    float3 P0 = math.transform(worldToLocal, GetPos(i0));
                    float3 P1 = math.transform(worldToLocal, GetPos(i1));
                    float3 P2 = math.transform(worldToLocal, GetPos(i2));
                    float3 P3 = math.transform(worldToLocal, GetPos(i3));

                    for (int step = 1; step <= resolution; step++)
                    {
                        float t = (float)step / resolution;
                        float3 pos = GetCatmullRomPosition(t, P0, P1, P2, P3);
                        float3 deriv = GetCatmullRomDerivative(t, P0, P1, P2, P3);
                        float derivLenSq = math.lengthsq(deriv);
                        float3 tangent;
                        if (derivLenSq > 1e-6f)
                            tangent = deriv * math.rsqrt(derivLenSq);
                        else
                            tangent = prevUntwistedTangent;

                        quaternion q = FromToRotation(prevUntwistedTangent, tangent);
                        float3 untwistedNormal = math.rotate(q, prevUntwistedNormal);
                        
                        float3 rawUb = math.cross(tangent, untwistedNormal);
                        float ubLenSq = math.lengthsq(rawUb);
                        float3 untwistedBinormal = (ubLenSq > 1e-6f) ? (rawUb * math.rsqrt(ubLenSq)) : new float3(1, 0, 0);
                        untwistedNormal = math.cross(untwistedBinormal, tangent);
                        float unLenSq = math.lengthsq(untwistedNormal);
                        if (unLenSq > 1e-6f) untwistedNormal *= math.rsqrt(unLenSq);
                        else untwistedNormal = prevUntwistedNormal;

                        accumulatedLen += math.distance(pos, prevPos);
                        prevPos = pos;
                        
                        // Prevent buffer overflow if precision issues cause extra frame
                        if (frameIndex < frames.Length)
                        {
                            // Apply Twist
                            float currentTwist = (accumulatedLen * twistRadiansPerUnit) + baseTwistRadians;
                            quaternion twistRot = quaternion.AxisAngle(tangent, currentTwist);
                            
                            float3 finalNormal = math.rotate(twistRot, untwistedNormal);
                            float3 rawFinalBinormal = math.cross(tangent, finalNormal);
                            float fbLenSq = math.lengthsq(rawFinalBinormal);
                            float3 finalBinormal = (fbLenSq > 1e-6f) ? (rawFinalBinormal * math.rsqrt(fbLenSq)) : untwistedBinormal;

                            frames[frameIndex] = new RopeFrame
                            {
                                position = pos,
                                tangent = tangent,
                                normal = finalNormal,
                                binormal = finalBinormal,
                                vCoord = (accumulatedLen * textureTiling) + uvOffset
                            };
                            frameIndex++;

                            prevUntwistedNormal = untwistedNormal;
                            prevUntwistedTangent = tangent;
                        }
                    }
                }
            }

            private float3 GetPos(int index)
            {
                return positions[index];
            }
            
            private float3 CalculateNormalFromGlobal(float3 tangent)
            {
                float3 globalUp = new float3(0, 1, 0);
                if (math.abs(math.dot(tangent, globalUp)) > 0.99f) globalUp = new float3(0, 0, 1);
                float3 c = math.cross(tangent, globalUp);
                float lenSq = math.lengthsq(c);
                if (lenSq > 1e-6f) return c * math.rsqrt(lenSq);
                return new float3(1, 0, 0);
            }

            private float3 GetCatmullRomPosition(float t, float3 p0, float3 p1, float3 p2, float3 p3)
            {
                float t2 = t * t; float t3 = t2 * t;
                return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
            }
            
            private float3 GetCatmullRomDerivative(float t, float3 p0, float3 p1, float3 p2, float3 p3)
            {
                float t2 = t * t;
                return 0.5f * ((p2 - p0) + (2f * p0 - 5f * p1 + 4f * p2 - p3) * 2f * t + (-p0 + 3f * p1 - 3f * p2 + p3) * 3f * t2);
            }

            private quaternion FromToRotation(float3 from, float3 to)
            {
                float fromLenSq = math.lengthsq(from);
                float toLenSq = math.lengthsq(to);
                if (fromLenSq < 1e-6f || toLenSq < 1e-6f) return quaternion.identity;

                float3 f = from * math.rsqrt(fromLenSq);
                float3 t = to * math.rsqrt(toLenSq);
                float dot = math.clamp(math.dot(f, t), -1f, 1f);
                if (dot > 0.99999f) return quaternion.identity;
                if (dot < -0.99999f) 
                {
                    float3 axis = math.cross(new float3(1,0,0), f);
                    if (math.lengthsq(axis) < 0.001f) axis = math.cross(new float3(0,1,0), f);
                    float axisLenSq = math.lengthsq(axis);
                    if (axisLenSq > 1e-6f)
                        return quaternion.AxisAngle(axis * math.rsqrt(axisLenSq), math.PI);
                    return quaternion.identity;
                }
                float3 c = math.cross(f, t);
                float s = math.sqrt((1f + dot) * 2f);
                if (s < 1e-6f) return quaternion.identity;
                float invS = 1f / s;
                return new quaternion(c.x * invS, c.y * invS, c.z * invS, s * 0.5f);
            }
        }
        
        [BurstCompile]
        struct GenerateMeshVerticesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RopeFrame> frames;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<RopeVertex> vertices;
            public RopeGeometryType geometryType;
            public float radius;
            public int segments;
            public int strandCount;
            public float strandOffset;
            public float strandRadius;
            public float twistRadiansPerUnit;

            public void Execute(int i)
            {
                RopeFrame frame = frames[i];
                if (geometryType == RopeGeometryType.Single)
                {
                    int ringStart = i * (segments + 1);
                    for (int s = 0; s <= segments; s++)
                    {
                        float u = (float)s / segments;
                        float angle = u * math.PI * 2;
                        float3 localDir = (frame.normal * math.cos(angle)) + (frame.binormal * math.sin(angle));
                        float3 meshTangent = (-frame.normal * math.sin(angle)) + (frame.binormal * math.cos(angle));
                        vertices[ringStart + s] = new RopeVertex
                        {
                            position = frame.position + (localDir * radius),
                            normal = localDir,
                            tangent = new float4(meshTangent, 1.0f),
                            uv = new float2(u, frame.vCoord)
                        };
                    }
                }
                else
                {
                    int ringStart = i * strandCount * (segments + 1);
                    float spiralStretchFactor = math.sqrt(1f + math.pow(strandOffset * twistRadiansPerUnit, 2f));
                    float adjustedVCoord = frame.vCoord * spiralStretchFactor;

                    for (int k = 0; k < strandCount; k++)
                    {
                        float strandAngle = strandCount > 1 ? ((float)k / strandCount * math.PI * 2) : 0f;
                        
                        float3 offsetDir = (frame.normal * math.cos(strandAngle)) + (frame.binormal * math.sin(strandAngle));
                        float3 strandCenter = frame.position + (offsetDir * strandOffset);
                        
                        float3 tangentOffsetDir = (-frame.normal * math.sin(strandAngle)) + (frame.binormal * math.cos(strandAngle));
                        float3 rawStrandTangent = frame.tangent + strandOffset * twistRadiansPerUnit * tangentOffsetDir;
                        float stLenSq = math.lengthsq(rawStrandTangent);
                        float3 trueStrandTangent = stLenSq > 1e-6f ? rawStrandTangent * math.rsqrt(stLenSq) : frame.tangent;
                        
                        float3 rawStrandNormal = offsetDir - math.dot(offsetDir, trueStrandTangent) * trueStrandTangent;
                        float snLenSq = math.lengthsq(rawStrandNormal);
                        float3 trueStrandNormal = snLenSq > 1e-6f ? rawStrandNormal * math.rsqrt(snLenSq) : frame.normal;
                        float3 trueStrandBinormal = math.cross(trueStrandTangent, trueStrandNormal);
                        
                        int strandRingStart = ringStart + k * (segments + 1);
                        for (int s = 0; s <= segments; s++)
                        {
                            float u = (float)s / segments;
                            float angle = u * math.PI * 2;
                            
                            float3 localDir = (trueStrandNormal * math.cos(angle)) + (trueStrandBinormal * math.sin(angle));
                            float3 meshTangent = (-trueStrandNormal * math.sin(angle)) + (trueStrandBinormal * math.cos(angle));
                            
                            vertices[strandRingStart + s] = new RopeVertex
                            {
                                position = strandCenter + (localDir * strandRadius),
                                normal = localDir,
                                tangent = new float4(meshTangent, 1.0f),
                                uv = new float2(u, adjustedVCoord)
                            };
                        }
                    }
                }
            }
        }

        [BurstCompile]
        struct GenerateCapsJob : IJob
        {
            [ReadOnly] public NativeArray<RopeFrame> frames;
            [NativeDisableParallelForRestriction] public NativeArray<RopeVertex> vertices;
            public RopeGeometryType geometryType;
            public int strandCount;
            public float strandOffset;
            public float twistRadiansPerUnit;
            public int startCapIndex;
            public int endCapIndex;

            public void Execute()
            {
                if (frames.Length == 0) return;
                int sCount = (geometryType == RopeGeometryType.MultiStrand) ? strandCount : 1;

                // Start Cap(s)
                RopeFrame startFrame = frames[0];
                for (int k = 0; k < sCount; k++)
                {
                    float3 pos = startFrame.position;
                    float3 trueNormal = -startFrame.tangent;
                    float3 tang = startFrame.binormal;
                    
                    if (geometryType == RopeGeometryType.MultiStrand)
                    {
                        float strandAngle = strandCount > 1 ? ((float)k / strandCount * math.PI * 2) : 0f;
                        float3 offset = (startFrame.normal * math.cos(strandAngle)) + (startFrame.binormal * math.sin(strandAngle));
                        pos += offset * strandOffset;
                        
                        float3 tangentOffsetDir = (-startFrame.normal * math.sin(strandAngle)) + (startFrame.binormal * math.cos(strandAngle));
                        float3 rawTangent = startFrame.tangent + strandOffset * twistRadiansPerUnit * tangentOffsetDir;
                        float rtLenSq = math.lengthsq(rawTangent);
                        trueNormal = rtLenSq > 1e-6f ? -(rawTangent * math.rsqrt(rtLenSq)) : -startFrame.tangent;
                    }
                    
                    vertices[startCapIndex + k] = new RopeVertex
                    {
                        position = pos,
                        normal = trueNormal,
                        tangent = new float4(tang, 1.0f),
                        uv = new float2(0.5f, 0.5f)
                    };
                }

                // End Cap(s)
                RopeFrame endFrame = frames[frames.Length - 1];
                for (int k = 0; k < sCount; k++)
                {
                    float3 pos = endFrame.position;
                    float3 trueNormal = endFrame.tangent;
                    float3 tang = endFrame.binormal;
                    
                    if (geometryType == RopeGeometryType.MultiStrand)
                    {
                        float strandAngle = strandCount > 1 ? ((float)k / strandCount * math.PI * 2) : 0f;
                        float3 offset = (endFrame.normal * math.cos(strandAngle)) + (endFrame.binormal * math.sin(strandAngle));
                        pos += offset * strandOffset;
                        
                        float3 tangentOffsetDir = (-endFrame.normal * math.sin(strandAngle)) + (endFrame.binormal * math.cos(strandAngle));
                        float3 rawTangent = endFrame.tangent + strandOffset * twistRadiansPerUnit * tangentOffsetDir;
                        float rtLenSq = math.lengthsq(rawTangent);
                        trueNormal = rtLenSq > 1e-6f ? (rawTangent * math.rsqrt(rtLenSq)) : endFrame.tangent;
                    }

                    vertices[endCapIndex + k] = new RopeVertex
                    {
                        position = pos,
                        normal = trueNormal,
                        tangent = new float4(tang, 1.0f),
                        uv = new float2(0.5f, 0.5f)
                    };
                }
            }
        }
    }
}
