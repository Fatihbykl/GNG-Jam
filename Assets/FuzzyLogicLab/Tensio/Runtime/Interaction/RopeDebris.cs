using UnityEngine;
using System.Collections;

namespace Tensio
{
    [AddComponentMenu("Tensio/Interaction/Rope Debris")]
    [RequireComponent(typeof(MeshRenderer))]
    public class RopeDebris : MonoBehaviour
    {
        private float _lifetime;
        private float _fadeTime;
        private RopeController _controller;
        private RopeRenderer _ropeRenderer;
        private RopeAudio _ropeAudio;
        private RopeWinch _winch;
        
        private float _originalRadius;
        private RopeController.RopeInitSettings.DebrisMode _mode;

        public void Initialize(float lifetime, float fadeTime, RopeController.RopeInitSettings.DebrisMode mode)
        {
            _lifetime = lifetime;
            _fadeTime = fadeTime;
            _mode = mode;
            _controller = GetComponent<RopeController>();
            _ropeRenderer = GetComponent<RopeRenderer>();
            _ropeAudio = GetComponent<RopeAudio>();
            _winch = GetComponent<RopeWinch>();
            
            if (_mode == RopeController.RopeInitSettings.DebrisMode.ShrinkAndDestroy)
            {
                if (_ropeRenderer != null)
                {
                    _originalRadius = _ropeRenderer.Radius;
                }
                StartCoroutine(ShrinkAndDestroyRoutine());
            }
            else if (_mode == RopeController.RopeInitSettings.DebrisMode.ConvertToRigidbody)
            {
                StartCoroutine(ConvertToRigidbodyRoutine());
            }
        }

        private IEnumerator ShrinkAndDestroyRoutine()
        {
            float waitTime = Mathf.Max(0f, _lifetime - _fadeTime);
            yield return new WaitForSeconds(waitTime);

            float actualFadeTime = Mathf.Min(_lifetime, _fadeTime);
            float elapsed = 0f;

            while (elapsed < actualFadeTime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / actualFadeTime;
                
                if (_ropeRenderer != null)
                {
                    _ropeRenderer.Radius = Mathf.Lerp(_originalRadius, 0f, t);
                }
                
                yield return null;
            }

            if (_controller != null) Destroy(_controller.gameObject);
            else Destroy(gameObject);
        }

        private IEnumerator ConvertToRigidbodyRoutine()
        {
            yield return new WaitForEndOfFrame();

            Vector3 avgVelocity = Vector3.zero;
            if (_controller != null && _controller.Solver != null && _controller.Solver.IsInitialized)
            {
                var velocities = _controller.Solver.Velocities;
                for (int i = 0; i < velocities.Length; i++)
                {
                    avgVelocity += (Vector3)velocities[i];
                }
                if (velocities.Length > 0) avgVelocity /= velocities.Length;
            }

            MeshFilter filter = GetComponent<MeshFilter>();
            Mesh finalMesh = null;
            
            if (_ropeRenderer != null)
            {
                finalMesh = _ropeRenderer.BakeMesh();
                if (filter != null && finalMesh != null)
                {
                    filter.sharedMesh = finalMesh;
                }
            }
            else if (filter != null && filter.sharedMesh != null)
            {
                finalMesh = Instantiate(filter.sharedMesh);
                filter.sharedMesh = finalMesh;
            }
            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                meshRenderer.enabled = true;
            }

            if (_ropeRenderer != null) Destroy(_ropeRenderer);
            if (_ropeAudio != null) Destroy(_ropeAudio);
            if (_winch != null) Destroy(_winch);
            if (_controller != null) Destroy(_controller);

            if (finalMesh != null)
            {
                // Center the pivot of the mesh so it shrinks properly
                Vector3 localCenter = finalMesh.bounds.center;
                Vector3[] vertices = finalMesh.vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] -= localCenter;
                }
                finalMesh.vertices = vertices;
                finalMesh.RecalculateBounds();
                
                // Move transform to compensate so it doesn't jump
                transform.position = transform.TransformPoint(localCenter);

                MeshCollider mc = gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = finalMesh;
                mc.convex = true;

                Rigidbody rb = gameObject.AddComponent<Rigidbody>();
#if UNITY_6000_0_OR_NEWER
                rb.linearVelocity = avgVelocity;
#else
                rb.velocity = avgVelocity;
#endif
                
                rb.angularVelocity = new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f));
            }

            float waitTime = Mathf.Max(0f, _lifetime - _fadeTime);
            yield return new WaitForSeconds(waitTime);

            float actualFadeTime = Mathf.Min(_lifetime, _fadeTime);
            float elapsed = 0f;
            Vector3 originalScale = transform.localScale;

            while (elapsed < actualFadeTime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / actualFadeTime;
                
                transform.localScale = Vector3.Lerp(originalScale, Vector3.zero, t);
                
                yield return null;
            }
            
            Destroy(gameObject);
        }
    }
}
