using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;

namespace Tensio
{
    /// <summary>
    /// Global registry for all rope-compatible colliders.
    /// Maintains static lists of proxies to avoid runtime searches.
    /// </summary>
    public static class RopeCollisionManager
    {
        private static readonly List<RopeSphereProxy> _spheres = new List<RopeSphereProxy>();
        private static readonly List<RopeBoxProxy> _boxes = new List<RopeBoxProxy>();
        private static readonly List<RopeCapsuleProxy> _capsules = new List<RopeCapsuleProxy>();
        private static readonly List<RopePlaneProxy> _planes = new List<RopePlaneProxy>();

        #region Registration

        public static void Register(RopeSphereProxy proxy) { if (!_spheres.Contains(proxy)) _spheres.Add(proxy); }
        public static void Unregister(RopeSphereProxy proxy) { _spheres.Remove(proxy); }

        public static void Register(RopeBoxProxy proxy) { if (!_boxes.Contains(proxy)) _boxes.Add(proxy); }
        public static void Unregister(RopeBoxProxy proxy) { _boxes.Remove(proxy); }

        public static void Register(RopeCapsuleProxy proxy) { if (!_capsules.Contains(proxy)) _capsules.Add(proxy); }
        public static void Unregister(RopeCapsuleProxy proxy) { _capsules.Remove(proxy); }

        public static void Register(RopePlaneProxy proxy) { if (!_planes.Contains(proxy)) _planes.Add(proxy); }
        public static void Unregister(RopePlaneProxy proxy) { _planes.Remove(proxy); }

        #endregion

        #region Data Retrieval

        public static void GetSphereData(ref NativeArray<SphereColliderData> buffer, Allocator allocator = Allocator.Persistent)
        {
            EnsureCapacity(ref buffer, _spheres.Count, allocator);
            for (int i = 0; i < _spheres.Count; i++) buffer[i] = _spheres[i].Data;
        }

        public static void GetBoxData(ref NativeArray<BoxColliderData> buffer, Allocator allocator = Allocator.Persistent)
        {
            EnsureCapacity(ref buffer, _boxes.Count, allocator);
            for (int i = 0; i < _boxes.Count; i++) buffer[i] = _boxes[i].Data;
        }

        public static void GetCapsuleData(ref NativeArray<CapsuleColliderData> buffer, Allocator allocator = Allocator.Persistent)
        {
            EnsureCapacity(ref buffer, _capsules.Count, allocator);
            for (int i = 0; i < _capsules.Count; i++) buffer[i] = _capsules[i].Data;
        }

        public static void GetPlaneData(ref NativeArray<PlaneColliderData> buffer, Allocator allocator = Allocator.Persistent)
        {
            EnsureCapacity(ref buffer, _planes.Count, allocator);
            for (int i = 0; i < _planes.Count; i++) buffer[i] = _planes[i].Data;
        }

        /// <summary>
        /// Ensures the buffer is the correct size. Re-allocates if necessary.
        /// </summary>
        private static void EnsureCapacity<T>(ref NativeArray<T> buffer, int count, Allocator allocator) where T : struct
        {
            if (!buffer.IsCreated || buffer.Length != count)
            {
                if (buffer.IsCreated) buffer.Dispose();
                buffer = new NativeArray<T>(count, allocator);
            }
        }

        #endregion
        
        #region Impulse Application (Two-Way Coupling)

        /// <summary>
        /// Used by the RopeSolver to pass forces back from the XPBD simulation to standard Unity Rigidbodies.
        /// </summary>
        public static void ApplyProxyImpulse(int type, int index, Vector3 impulse, Vector3 point, float multiplier)
        {
            if (multiplier <= 0f) return;

            Rigidbody rb = null;
            if (type == 0 && index < _spheres.Count) rb = _spheres[index].GetComponentInParent<Rigidbody>();
            else if (type == 1 && index < _capsules.Count) rb = _capsules[index].GetComponentInParent<Rigidbody>();
            else if (type == 2 && index < _boxes.Count) rb = _boxes[index].GetComponentInParent<Rigidbody>();
            else if (type == 3 && index < _planes.Count) rb = _planes[index].GetComponentInParent<Rigidbody>();

            if (rb != null && !rb.isKinematic)
            {
                // Scale the impulse internally by 0.001f so that a multiplier of 1.0f in the inspector is reasonable.
                rb.AddForceAtPosition(impulse * multiplier * 0.0001f, point, ForceMode.Impulse);
            }
        }

        #endregion
    }
}