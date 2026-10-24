using UnityEngine;
using Unity.Mathematics;

namespace Tensio
{
    [ExecuteAlways]
    [AddComponentMenu("Tensio/Interaction/Proxies/Rope Sphere Proxy")]
    public class RopeSphereProxy : MonoBehaviour
    {
        [SerializeField] private SphereCollider _sourceCollider;
        
        [System.NonSerialized]
        public SphereColliderData Data;

        private void OnEnable()
        {
            RopeCollisionManager.Register(this);
            if (!_sourceCollider) _sourceCollider = GetComponent<SphereCollider>();
            SyncData();
        }

        private void OnDisable() => RopeCollisionManager.Unregister(this);

        private void Update()
        {
            if (transform.hasChanged || !Application.isPlaying) SyncData();
        }

        private void OnValidate()
        {
            if (!_sourceCollider) _sourceCollider = GetComponent<SphereCollider>();
            SyncData();
        }

        public void SyncData()
        {
            if (_sourceCollider == null) return;

            float maxScale = Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Max(Mathf.Abs(transform.lossyScale.y), Mathf.Abs(transform.lossyScale.z)));
            
            Data = new SphereColliderData
            {
                Position = transform.TransformPoint(_sourceCollider.center),
                Radius = _sourceCollider.radius * maxScale
            };
        }

        private void OnDrawGizmosSelected()
        {
            if (_sourceCollider == null) return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(Data.Position, Data.Radius);
        }
    }
}