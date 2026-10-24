using UnityEngine;
using UnityEngine.Audio;

namespace Tensio
{
    /// <summary>
    /// Configuration asset for Rope Audio types (e.g., Chain, Silk, Cable).
    /// </summary>
    [CreateAssetMenu(fileName = "NewRopeAudioProfile", menuName = "Tensio/Audio Profile")]
    public class RopeAudioProfile : ScriptableObject
    {
        [Header("Global Settings")]
        [Tooltip("The mixer group to route all rope sounds through.")]
        [SerializeField] private AudioMixerGroup mixerGroup;

        [Header("Tension (Stretching)")]
        [Tooltip("Looping sound when the rope is under stress.")]
        [SerializeField] private AudioClip tensionLoop;
        
        [Range(0f, 1f)] 
        [SerializeField] private float maxTensionVolume = 0.5f;
        
        [Tooltip("Pitch multiplier at maximum stress.")]
        [Range(1f, 3f)] 
        [SerializeField] private float maxTensionPitch = 1.5f;
        
        [Tooltip("Stress ratio (CurrentLen / RestLen) where sound begins.")]
        [SerializeField] private float stressThreshold = 1.05f;

        [Tooltip("Stress ratio where sound reaches maximum volume and pitch.")]
        [SerializeField] private float maxStressRatio = 1.5f;

        [Header("Aerodynamics (Whoosh)")]
        [Tooltip("Looping sound when the rope moves fast.")]
        [SerializeField] private AudioClip windLoop;
        
        [Range(0f, 1f)] 
        [SerializeField] private float maxWindVolume = 1.0f;
        
        [Tooltip("Velocity magnitude required to reach max volume.")]
        [SerializeField] private float maxWindVelocity = 10.0f;

        [Header("Impacts")]
        [Tooltip("Randomly chosen sounds when hitting colliders.")]
        [SerializeField] private AudioClip[] impactClips;
        
        [Range(0f, 1f)] 
        [SerializeField] private float impactVolume = 0.8f;
        
        [Tooltip("Minimum velocity required to trigger an impact sound.")]
        [SerializeField] private float minImpactVelocity = 1.0f;

        [Tooltip("Velocity magnitude required to reach max impact volume.")]
        [SerializeField] private float maxImpactVelocity = 10.0f;
        
        [Tooltip("Seconds between impact sounds to prevent spamming.")]
        [SerializeField] private float impactCooldown = 0.15f;

        [Header("Breaking")]
        [SerializeField] private AudioClip snapSound;
        
        [Range(0f, 1f)] 
        [SerializeField] private float snapVolume = 1.0f;

        [Header("3D Spatial Settings")]
        [SerializeField] private AudioRolloffMode rolloffMode = AudioRolloffMode.Logarithmic;
        [SerializeField] private float minDistance = 1.0f;
        [SerializeField] private float maxDistance = 20.0f;

        // Public Accessors
        public AudioMixerGroup MixerGroup => mixerGroup;

        public AudioClip TensionLoop => tensionLoop;
        public float MaxTensionVolume => maxTensionVolume;
        public float MaxTensionPitch => maxTensionPitch;
        public float StressThreshold => stressThreshold;
        public float MaxStressRatio => maxStressRatio;

        public AudioClip WindLoop => windLoop;
        public float MaxWindVolume => maxWindVolume;
        public float MaxWindVelocity => maxWindVelocity;

        public AudioClip[] ImpactClips => impactClips;
        public float ImpactVolume => impactVolume;
        public float MinImpactVelocity => minImpactVelocity;
        public float MaxImpactVelocity => maxImpactVelocity;
        public float ImpactCooldown => impactCooldown;

        public AudioClip SnapSound => snapSound;
        public float SnapVolume => snapVolume;

        public AudioRolloffMode RolloffMode => rolloffMode;
        public float MinDistance => minDistance;
        public float MaxDistance => maxDistance;
    }
}