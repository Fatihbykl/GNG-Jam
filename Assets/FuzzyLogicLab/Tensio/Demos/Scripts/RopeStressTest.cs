using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Tensio;

namespace Tensio.Demos
{
    [AddComponentMenu("Tensio/Demo/Rope Stress Test")]
    public class RopeStressTest : MonoBehaviour
    {
        [Header("Grid Settings")]
        [Tooltip("Number of ropes along the X axis")]
        public int rows = 10;
        [Tooltip("Number of ropes along the Z axis")]
        public int columns = 10;
        [Tooltip("Distance between ropes")]
        public float spacing = 2.0f;

        [Header("Rope Configuration")]
        public int particleCount = 20;
        public float length = 5.0f;
        [Range(0f, 1f)] public float compliance = 0.0f;
        public int subSteps = 4;
        public bool enableSelfCollision = false;

        [Header("Rendering")]
        public GameObject ropePrefab;
        public Material ropeMaterial;
        
        [Header("Camera Control")]
        public float cameraSpeed = 10.0f;

        private List<RopeController> _ropes = new List<RopeController>();
        private RopeLODController _monitoredLOD;

        void Start()
        {
            SpawnGrid();
        }

        void SpawnGrid()
        {
            Vector3 startPos = transform.position;

            float gridWidth = (rows - 1) * spacing;
            float gridDepth = (columns - 1) * spacing;
            Vector3 offset = new Vector3(-gridWidth / 2f, 0, -gridDepth / 2f);

            for (int x = 0; x < rows; x++)
            {
                for (int z = 0; z < columns; z++)
                {
                    Vector3 pos = startPos + offset + new Vector3(x * spacing, 0, z * spacing);
                    CreateRope(pos, $"Rope_{x}_{z}");
                }
            }
        }

        void CreateRope(Vector3 position, string name)
        {
            GameObject go;
            RopeController rope;

            if (ropePrefab != null)
            {
                go = Instantiate(ropePrefab, position, Quaternion.identity, transform);
                go.name = name;
                rope = go.GetComponent<RopeController>();
                if (rope == null) rope = go.AddComponent<RopeController>();
            }
            else
            {
                go = new GameObject(name);
                go.transform.position = position;
                go.transform.parent = this.transform;

                RopeRenderer ropeRenderer = go.AddComponent<RopeRenderer>();
                rope = go.GetComponent<RopeController>();

                if (ropeMaterial != null)
                {
                    ropeRenderer.Material = ropeMaterial;
                }
            }

            rope.InitSettings.Length = length;
            rope.InitSettings.ParticleCount = particleCount;
            rope.InitSettings.PinFirstParticle = true;

            rope.Settings.SubSteps = subSteps;
            rope.Settings.Compliance = compliance;
            rope.Settings.SupportInterRopeCollision = enableSelfCollision;

            rope.InitializeRope();
            
            _ropes.Add(rope);

            if (_monitoredLOD == null)
            {
                _monitoredLOD = rope.GetComponent<RopeLODController>();
                if (_monitoredLOD == null) 
                    _monitoredLOD = rope.gameObject.AddComponent<RopeLODController>();
            }
        }

        void Update()
        {
            if (Camera.main != null)
            {
                float zMovement = 0f;
#if ENABLE_INPUT_SYSTEM
                bool upPressed = Keyboard.current != null && Keyboard.current.upArrowKey.isPressed;
                bool downPressed = Keyboard.current != null && Keyboard.current.downArrowKey.isPressed;
#else
                bool upPressed = Input.GetKey(KeyCode.UpArrow);
                bool downPressed = Input.GetKey(KeyCode.DownArrow);
#endif

                if (upPressed)
                {
                    zMovement = 1f;
                }
                else if (downPressed)
                {
                    zMovement = -1f;
                }

                if (zMovement != 0)
                {
                    Camera.main.transform.position += new Vector3(0, 0, zMovement * cameraSpeed * Time.deltaTime);
                }
            }
        }

        void OnGUI()
        {
            if (_monitoredLOD != null)
            {
                float w = 250;
                float h = 120;
                Rect rect = new Rect(Screen.width - w - 10, 10, w, h);
                
                GUI.Box(rect, "LOD Status (First Rope)");
                
                GUILayout.BeginArea(rect);
                GUILayout.Space(25);
                GUILayout.BeginHorizontal();
                GUILayout.Space(10);
                GUILayout.BeginVertical();
                
                GUILayout.Label($"LOD Level: {_monitoredLOD.CurrentLOD}");
                GUILayout.Label($"Distance: {_monitoredLOD.CurrentDistance:F2} m");
                GUILayout.Label($"Status: {_monitoredLOD.DebugStatus}");
                GUILayout.Label($"Thresholds: {_monitoredLOD.DistanceMeshToLine:F0} / {_monitoredLOD.DistanceLineToCull:F0} / {_monitoredLOD.DistanceSuspendPhysics:F0}");
                
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
            }
        }
    }
}
