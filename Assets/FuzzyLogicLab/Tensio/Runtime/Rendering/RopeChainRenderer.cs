using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;

namespace Tensio
{
    [DefaultExecutionOrder(250)]
    [AddComponentMenu("Tensio/Rendering/Rope Chain Renderer")]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [RequireComponent(typeof(RopeController))]
    public class RopeChainRenderer : MonoBehaviour
    {
        [Header("Chain Settings")]
        [SerializeField] private Mesh linkMesh;
        [SerializeField] private Material linkMaterial;
        [SerializeField] private float linkSpacing = 0.2f;
        [SerializeField] private Vector3 linkScale = Vector3.one;
        [SerializeField] private bool alternatingRotation = true;
        [SerializeField] private bool useBatching = true;
        [SerializeField] private float linkOffset = 0f;

        public float LinkOffset { get => linkOffset; set => linkOffset = value; }
        public float LinkSpacing { get => linkSpacing; set => linkSpacing = value; }
        public Mesh LinkMesh { get => linkMesh; set => linkMesh = value; }
        public Material LinkMaterial { get => linkMaterial; set => linkMaterial = value; }
        private bool _materialWarningLogged = false;

        private void OnValidate()
        {
            if (linkMesh != null && linkMaterial != null)
            {
                _materialWarningLogged = false;
            }
        }

        private RopeController _rope;
        private Matrix4x4[] _matrices;
        private int _activeLinkCount = 0;
        private MaterialPropertyBlock _props;

        private Quaternion _lastFirstLinkRotation = Quaternion.identity;
        private bool _hasLastRotation = false;

        public Mesh BakeMesh()
        {
            if (linkMesh == null || _matrices == null || _activeLinkCount == 0) return null;

            CombineInstance[] combines = new CombineInstance[_activeLinkCount];
            Matrix4x4 invRopeTransform = transform.worldToLocalMatrix;

            for (int i = 0; i < _activeLinkCount; i++)
            {
                combines[i].mesh = linkMesh;
                combines[i].transform = invRopeTransform * _matrices[i];
            }

            Mesh bakedMesh = new Mesh();
            bakedMesh.name = "BakedChainMesh";
            if (linkMesh.vertexCount * _activeLinkCount > 65535)
                bakedMesh.indexFormat = IndexFormat.UInt32;
            
            bakedMesh.CombineMeshes(combines, true, true);
            return bakedMesh;
        }

        private void Start()
        {
            _rope = GetComponent<RopeController>();
            _props = new MaterialPropertyBlock();
            
            if (_rope != null && _rope.EditorPreviewPositions != null && _rope.EditorPreviewPositions.Length > 1)
            {
                var positions = new NativeArray<float3>(_rope.EditorPreviewPositions.Length, Allocator.Temp);
                for (int i = 0; i < _rope.EditorPreviewPositions.Length; i++)
                {
                    positions[i] = _rope.EditorPreviewPositions[i];
                }
                
                DrawChain(positions);
                positions.Dispose();
            }
        }

        private void LateUpdate()
        {
            if (_rope == null || _rope.Solver == null || !_rope.Solver.IsInitialized) return;
            if (linkMesh == null || linkMaterial == null)
            {
                if (!_materialWarningLogged)
                {
                    Debug.LogError($"[Tensio] Missing Mesh or Material on RopeChainRenderer ({gameObject.name})! Please assign Link Mesh and Link Material in the RopeChainRenderer component.", this);
                    _materialWarningLogged = true;
                }
                return;
            }
            _materialWarningLogged = false;

            var visualPositions = new NativeArray<float3>(_rope.Solver.Positions.Length, Allocator.TempJob);
            _rope.GetVisualPositions(visualPositions);

            DrawChain(visualPositions);
            visualPositions.Dispose();
        }

        private void DrawChain(NativeArray<float3> positions)
        {
            if (positions.Length < 2) return;
            
            int maxLinks = 1023; // GPU Instancing limit per batch usually
            if (_matrices == null || _matrices.Length != maxLinks) _matrices = new Matrix4x4[maxLinks];

            int linkCount = 0;
            
            // 1. Temporal Coherence (First Link)
            float3 p0_start = positions[0];
            float3 p1_start = positions[1];
            
            Vector3 startDir;
            if (math.distance(p0_start, p1_start) < 0.0001f)
            {
                startDir = Vector3.forward;
            }
            else
            {
                startDir = math.normalize(p1_start - p0_start);
            }
            
            Quaternion currentRotation;
            
            if (!_hasLastRotation)
            {
                if (startDir == Vector3.zero) startDir = Vector3.forward;
                currentRotation = Quaternion.LookRotation(startDir);
            }
            else
            {
                Vector3 prevForward = _lastFirstLinkRotation * Vector3.forward;
                Quaternion deltaToNewDir = Quaternion.FromToRotation(prevForward, startDir);
                currentRotation = deltaToNewDir * _lastFirstLinkRotation;
            }

            // Normalize to prevent drift over time
            currentRotation = currentRotation.normalized;

            // Update state for next frame
            _lastFirstLinkRotation = currentRotation;
            _hasLastRotation = true;

            // Track the forward vector of the current rotation as we move along the rope
            Vector3 currentForward = currentRotation * Vector3.forward;

            int segmentCount = _rope.InitSettings.IsClosedLoop ? positions.Length : positions.Length - 1;
            float currentDistAccumulator = 0f;
            float startPhase = linkOffset % linkSpacing;
            
            if (startPhase < 0) startPhase += linkSpacing;
            
            float nextLinkTargetDist = startPhase;

            for (int i = 0; i < segmentCount; i++)
            {
                float3 p0 = positions[i];
                float3 p1 = positions[(i + 1) % positions.Length];

                float segLen = math.distance(p0, p1);

                Vector3 dir;
                if (segLen < 0.0001f)
                {
                     dir = currentForward; 
                }
                else
                {
                     dir = math.normalize(p1 - p0);
                }

                // Align rotation to segment
                Quaternion segmentDelta = Quaternion.FromToRotation(currentForward, dir);
                currentRotation = segmentDelta * currentRotation;
                currentRotation = currentRotation.normalized;
                currentForward = dir;

                // Place links in this segment
                while (nextLinkTargetDist < currentDistAccumulator + segLen)
                {
                    if (linkCount >= maxLinks) break;

                    // Calculate local T on this segment
                    float distOnSeg = nextLinkTargetDist - currentDistAccumulator;
                    // Clamp for safety
                    float t = Mathf.Clamp01(distOnSeg / segLen);
                    
                    float3 pos = math.lerp(p0, p1, t);

                    // Alternating Rotation
                    Quaternion finalRot = currentRotation;
                    if (alternatingRotation && (linkCount % 2 == 1))
                    {
                        finalRot *= Quaternion.Euler(0, 0, 90);
                    }

                    _matrices[linkCount] = Matrix4x4.TRS(pos, finalRot, linkScale);
                    linkCount++;

                    nextLinkTargetDist += linkSpacing;
                }

                currentDistAccumulator += segLen;
                
                if (linkCount >= maxLinks) break;
            }

            if (linkCount > 0)
            {
                _activeLinkCount = linkCount;
                if (useBatching)
                {
                    if (RopeChainBatcher.Instance == null)
                    {
                         var go = new GameObject("RopeChainBatcher");
                         go.AddComponent<RopeChainBatcher>();
                    }
                    RopeChainBatcher.Instance.RegisterChunk(linkMesh, linkMaterial, _matrices, linkCount);
                }
                else
                {
                    Graphics.DrawMeshInstanced(linkMesh, 0, linkMaterial, _matrices, linkCount, _props, ShadowCastingMode.On, true);
                }
            }
            else
            {
                _activeLinkCount = 0;
            }
        }
    }
}