using UnityEngine;
using Unity.Mathematics;

namespace Tensio
{
    [ExecuteAlways]
    [AddComponentMenu("Tensio/Interaction/Proxies/Rope Plane Proxy")]
    public class RopePlaneProxy : MonoBehaviour
    {
        [System.NonSerialized]
        public PlaneColliderData Data;

        private void OnEnable()
        {
            RopeCollisionManager.Register(this);
            SyncData();
        }

        private void OnDisable() => RopeCollisionManager.Unregister(this);

        private void Update()
        {
            if (transform.hasChanged || !Application.isPlaying) SyncData();
        }

        private void SyncData()
        {
            Data = new PlaneColliderData
            {
                Normal = transform.up,
                PointOnPlane = transform.position
            };
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;

            Vector3 center = transform.position;
            Vector3 normal = transform.up;
            Vector3 tangent = (Mathf.Abs(normal.y) > 0.9f) ? Vector3.right : Vector3.Cross(normal, Vector3.up).normalized;
            Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
            
            // Reduced size for less clutter
            float size = 1f;
            Vector3 p0 = center + tangent * size + bitangent * size;
            Vector3 p1 = center + tangent * size - bitangent * size;
            Vector3 p2 = center - tangent * size - bitangent * size;
            Vector3 p3 = center - tangent * size + bitangent * size;

            // Simple quad outline
            Gizmos.DrawLine(p0, p1);
            Gizmos.DrawLine(p1, p2);
            Gizmos.DrawLine(p2, p3);
            Gizmos.DrawLine(p3, p0);
            
            // Normal indicator
            Gizmos.DrawLine(center, center + normal);
        }
    }
}