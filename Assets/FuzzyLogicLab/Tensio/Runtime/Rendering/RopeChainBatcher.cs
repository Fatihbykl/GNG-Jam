using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tensio
{
    [AddComponentMenu("Tensio/Rendering/Rope Chain Batcher")]
    [DefaultExecutionOrder(100)]
    public class RopeChainBatcher : MonoBehaviour
    {
        public static RopeChainBatcher Instance { get; private set; }

#if UNITY_2019_3_OR_NEWER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            Instance = null;
        }
#endif

        private readonly Dictionary<(Mesh, Material), List<Matrix4x4>> _batches = new Dictionary<(Mesh, Material), List<Matrix4x4>>();
        private readonly List<Matrix4x4[]> _matrixPool = new List<Matrix4x4[]>(); 

        private void Awake()
        {
            if (Instance != null && Instance != this) Destroy(this);
            else Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void RegisterChunk(Mesh mesh, Material material, Matrix4x4[] matrices, int count)
        {
            if (mesh == null || material == null || count == 0) return;

            var key = (mesh, material);
            if (!_batches.TryGetValue(key, out var list))
            {
                list = new List<Matrix4x4>(1024);
                _batches[key] = list;
            }
            
            for (int i = 0; i < count; i++)
            {
                list.Add(matrices[i]);
            }
        }

        private void LateUpdate()
        {
            // Draw all batches
            foreach (var kvp in _batches)
            {
                var (mesh, mat) = kvp.Key;
                var list = kvp.Value;
                
                int total = list.Count;
                int drawn = 0;

                while (drawn < total)
                {
                    int count = Mathf.Min(1023, total - drawn);
                    
                    var buffer = GetBuffer(count);
                    list.CopyTo(drawn, buffer, 0, count);
                    
                    Graphics.DrawMeshInstanced(mesh, 0, mat, buffer, count, null, ShadowCastingMode.On, true);
                    
                    drawn += count;
                }
                
                list.Clear();
            }
        }
        
        private Matrix4x4[] GetBuffer(int size)
        {
            if (_tempBuffer == null) _tempBuffer = new Matrix4x4[1023];
            return _tempBuffer;
        }
        
        private Matrix4x4[] _tempBuffer;
    }
}
