using System.Collections.Generic;
using UnityEngine;

namespace Tensio
{
    [AddComponentMenu("Tensio/Audio/Rope Audio")]
    [RequireComponent(typeof(RopeController))]
    public class RopeAudio : MonoBehaviour
    {
        [SerializeField] private RopeAudioProfile profile;
        [SerializeField] private float lerpSpeed = 5.0f;
        [SerializeField] private int impactPoolSize = 4;

        private RopeController _rope;
        private AudioSource _tensionSource;
        private AudioSource _windSource;
        private List<AudioSource> _impactPool = new List<AudioSource>();
        private int _poolIndex;
        private float _lastImpactTime;
        private Transform _listenerTransform;

        private void Awake()
        {
            _rope = GetComponent<RopeController>();
            SetupSources();
        }

        private void OnEnable()
        {
            if (_rope)
            {
                _rope.OnCollisionImpulse += HandleCollision;
                _rope.OnRopeCut += HandleCut;
            }
        }

        private void OnDisable()
        {
            if (_rope)
            {
                _rope.OnCollisionImpulse -= HandleCollision;
                _rope.OnRopeCut -= HandleCut;
            }
        }

        private void SetupSources()
        {
            if (!profile) return;

            if (profile.TensionLoop)
            {
                _tensionSource = CreateSource("Audio_Tension", profile.TensionLoop, true);
                _tensionSource.Play();
            }

            if (profile.WindLoop)
            {
                _windSource = CreateSource("Audio_Wind", profile.WindLoop, true);
                _windSource.Play();
            }

            // Create impact pool
            for (int i = 0; i < impactPoolSize; i++)
            {
                var src = CreateSource($"Audio_Impact_{i}", null, false);
                src.volume = 1f;
                _impactPool.Add(src);
            }
        }

        private AudioSource CreateSource(string name, AudioClip clip, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            
            src.clip = clip;
            src.loop = loop;
            src.volume = 0;
            src.spatialBlend = 1.0f;
            
            // Apply Profile settings
            if (profile)
            {
                src.outputAudioMixerGroup = profile.MixerGroup;
                src.minDistance = profile.MinDistance;
                src.maxDistance = profile.MaxDistance;
                src.rolloffMode = profile.RolloffMode;
            }

            return src;
        }

        private void Update()
        {
            if (!profile || !_rope) return;
            float dt = Time.deltaTime;

            UpdateSpatialPosition();

            if (_tensionSource)
            {
                float stress = _rope.CurrentStressRatio;
                float t = Mathf.Clamp01((stress - profile.StressThreshold) / (profile.MaxStressRatio - profile.StressThreshold));
                _tensionSource.volume = Mathf.Lerp(_tensionSource.volume, t > 0 ? Mathf.Lerp(0, profile.MaxTensionVolume, t) : 0, dt * lerpSpeed);
                _tensionSource.pitch = Mathf.Lerp(_tensionSource.pitch, Mathf.Lerp(1f, profile.MaxTensionPitch, t), dt * lerpSpeed);
            }

            if (_windSource)
            {
                float t = Mathf.Clamp01(_rope.AverageVelocity / profile.MaxWindVelocity);
                _windSource.volume = Mathf.Lerp(_windSource.volume, Mathf.Lerp(0, profile.MaxWindVolume, t), dt * lerpSpeed);
            }
        }

        private void UpdateSpatialPosition()
        {
            // Cache listener for performance
            if (!_listenerTransform)
            {
                var listener = FindAnyObjectByType<AudioListener>();
                if (listener) _listenerTransform = listener.transform;
                else return;
            }

            // Find the point on the rope closest to the listener to provide better 3D spatialization for long ropes.
            Vector3 listenerPos = _listenerTransform.position;
            Vector3 closestPoint = transform.position;
            float minSqrDist = float.MaxValue;

            var positions = _rope.Positions;
            if (!positions.IsCreated || positions.Length < 2) return;

            for (int i = 0; i < positions.Length - 1; i++)
            {
                Vector3 p0 = positions[i];
                Vector3 p1 = positions[i + 1];
                Vector3 segmentPoint = ClosestPointOnSegment(p0, p1, listenerPos);
                float sqrDist = (listenerPos - segmentPoint).sqrMagnitude;

                if (sqrDist < minSqrDist)
                {
                    minSqrDist = sqrDist;
                    closestPoint = segmentPoint;
                }
            }

            if (_tensionSource) _tensionSource.transform.position = closestPoint;
            if (_windSource) _windSource.transform.position = closestPoint;
        }

        private Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Vector3.Dot(ab, ab);
            return a + Mathf.Clamp01(t) * ab;
        }

        private void HandleCollision(Vector3 pos, float mag)
        {
            if (!profile || Time.time < _lastImpactTime + profile.ImpactCooldown || mag < profile.MinImpactVelocity) return;
            _lastImpactTime = Time.time;
            
            var clips = profile.ImpactClips;
            if (clips == null || clips.Length == 0) return;

            var src = _impactPool[_poolIndex];
            _poolIndex = (_poolIndex + 1) % _impactPool.Count;

            src.transform.position = pos;
            src.pitch = Random.Range(0.9f, 1.1f);
            
            float volume = profile.ImpactVolume * Mathf.Clamp01(mag / profile.MaxImpactVelocity);

            src.PlayOneShot(clips[Random.Range(0, clips.Length)], volume);
        }

        private void HandleCut()
        {
            if (!profile || !profile.SnapSound) return;

            // Use one of the pooled sources for the snap sound to ensure mixer routing
            var src = _impactPool[_poolIndex];
            _poolIndex = (_poolIndex + 1) % _impactPool.Count;

            src.transform.position = transform.position;
            src.pitch = 1.0f;
            src.PlayOneShot(profile.SnapSound, profile.SnapVolume);
        }
    }
}