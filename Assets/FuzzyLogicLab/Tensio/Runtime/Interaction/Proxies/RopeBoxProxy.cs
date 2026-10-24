using UnityEngine;
using Unity.Mathematics;

namespace Tensio
{
    [ExecuteAlways]
    [AddComponentMenu("Tensio/Interaction/Proxies/Rope Box Proxy")]
    public class RopeBoxProxy : MonoBehaviour
    {
        [SerializeField] private BoxCollider _sourceCollider;
        
        // Cached Data
        [System.NonSerialized]
        public BoxColliderData Data;

        private void OnEnable()
        {
            RopeCollisionManager.Register(this);
            if (!_sourceCollider) _sourceCollider = GetComponent<BoxCollider>();
            SyncData();
        }

        private void OnDisable() => RopeCollisionManager.Unregister(this);

        private void Update()
        {
            if (transform.hasChanged || !Application.isPlaying)
            {
                SyncData();
            }
        }

        private void OnValidate()
        {
            if (!_sourceCollider) _sourceCollider = GetComponent<BoxCollider>();
            SyncData();
        }

        public void SyncData()
        {
            if (_sourceCollider == null) return;

            Vector3 size = _sourceCollider.size;
            Vector3 scale = transform.lossyScale;
            Vector3 fullSize = new Vector3(
                Mathf.Abs(size.x * scale.x),
                Mathf.Abs(size.y * scale.y),
                Mathf.Abs(size.z * scale.z)
            );

            Data = new BoxColliderData
            {
                Center = transform.TransformPoint(_sourceCollider.center),
                Size = fullSize,
                Rotation = transform.rotation
            };
        }

        private void OnDrawGizmosSelected()
        {
            if (_sourceCollider == null) return;
            
            Gizmos.color = Color.cyan;
            Gizmos.matrix = Matrix4x4.TRS(Data.Center, Data.Rotation, Data.Size);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }
    }
}