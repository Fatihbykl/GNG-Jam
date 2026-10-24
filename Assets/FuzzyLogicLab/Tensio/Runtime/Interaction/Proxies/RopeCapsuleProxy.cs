using UnityEngine;
using Unity.Mathematics;

namespace Tensio
{
    [ExecuteAlways]
    [AddComponentMenu("Tensio/Interaction/Proxies/Rope Capsule Proxy")]
    public class RopeCapsuleProxy : MonoBehaviour
    {
        [SerializeField] private CapsuleCollider _sourceCollider;
        
        [System.NonSerialized]
        public CapsuleColliderData Data;

        private void OnEnable()
        {
            RopeCollisionManager.Register(this);
            if (!_sourceCollider) _sourceCollider = GetComponent<CapsuleCollider>();
            SyncData();
        }

        private void OnDisable() => RopeCollisionManager.Unregister(this);

        private void Update()
        {
            if (transform.hasChanged || !Application.isPlaying) SyncData();
        }

        private void OnValidate()
        {
            if (!_sourceCollider) _sourceCollider = GetComponent<CapsuleCollider>();
            SyncData();
        }

        public void SyncData()
        {
            if (_sourceCollider == null) return;
            
            // Logic copied from RopeController to ensure consistency
            // Radius scales with X/Z (usually they are uniform for capsule)
            float radiusScale = Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z));
            float radius = _sourceCollider.radius * radiusScale;
            
            // Height logic
            float height = Mathf.Max(0, _sourceCollider.height * Mathf.Abs(transform.lossyScale.y) - 2 * radius);

            Vector3 localUp = Vector3.up;
            if (_sourceCollider.direction == 0) localUp = Vector3.right; // X-axis
            else if (_sourceCollider.direction == 2) localUp = Vector3.forward; // Z-axis

            Vector3 center = transform.TransformPoint(_sourceCollider.center);
            Vector3 upDir = transform.TransformDirection(localUp).normalized;

            Data = new CapsuleColliderData
            {
                Start = center - upDir * (height * 0.5f),
                End = center + upDir * (height * 0.5f),
                Radius = radius
            };
        }

        private void OnDrawGizmosSelected()
        {
            if (_sourceCollider == null) return;
            Gizmos.color = Color.cyan;
            
            // Simple wireframe: Wire spheres at ends and a connecting line
            Gizmos.DrawWireSphere(Data.Start, Data.Radius);
            Gizmos.DrawWireSphere(Data.End, Data.Radius);
            Gizmos.DrawLine(Data.Start, Data.End);
        }
    }
}