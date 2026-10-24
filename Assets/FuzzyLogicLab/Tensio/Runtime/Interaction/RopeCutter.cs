using UnityEngine;
using System.Collections.Generic;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Tensio
{
    [AddComponentMenu("Tensio/Interaction/Rope Cutter")]
    [RequireComponent(typeof(LineRenderer))]
    public class RopeCutter : MonoBehaviour
    {
        [Tooltip("Key to trigger cut (Mouse Button 0 is standard for drag).")]
        [SerializeField] private int mouseButton = 0;

        [Tooltip("Max distance to cut.")]
        [SerializeField] private float maxDistance = 100f;

        [Header("Visuals")]
        [Tooltip("Distance from camera to draw the trail.")]
        [SerializeField] private float visualDepth = 10f;
        
        [Tooltip("Number of points in the trail.")]
        [SerializeField] private int trailLength = 10;
        
        [Tooltip("Width of the trail.")]
        [SerializeField] private float trailWidth = 0.1f;
        
        [Tooltip("Color of the trail.")]
        [SerializeField] private Color trailColor = Color.red;

        [Header("Performance")]
        [Tooltip("Minimum time between cuts to prevent spamming.")]
        [SerializeField] private float minCutInterval = 0.05f;

        private LineRenderer _lineRenderer;
        private Queue<Vector3> _points = new Queue<Vector3>();
        private float _lastCutTime;

        private void Start()
        {
            _lineRenderer = GetComponent<LineRenderer>();
            _lineRenderer.positionCount = 0;
            _lineRenderer.startWidth = trailWidth;
            _lineRenderer.endWidth = 0f;
            _lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _lineRenderer.startColor = trailColor;
            _lineRenderer.endColor = new Color(trailColor.r, trailColor.g, trailColor.b, 0f);
            _lineRenderer.numCapVertices = 4;
            _lineRenderer.useWorldSpace = true;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            bool buttonPressed = false;
            if (Mouse.current != null)
            {
                if (mouseButton == 0) buttonPressed = Mouse.current.leftButton.isPressed;
                else if (mouseButton == 1) buttonPressed = Mouse.current.rightButton.isPressed;
                else if (mouseButton == 2) buttonPressed = Mouse.current.middleButton.isPressed;
            }
#else
            bool buttonPressed = Input.GetMouseButton(mouseButton);
#endif

            if (buttonPressed)
            {
                UpdateTrail();
                
                if (Time.time > _lastCutTime + minCutInterval)
                {
                    Cut();
                    _lastCutTime = Time.time;
                }
            }
            else
            {
                if (_points.Count > 0)
                {
                    _points.Clear();
                    _lineRenderer.positionCount = 0;
                }
            }
        }

        private void UpdateTrail()
        {
#if ENABLE_INPUT_SYSTEM
            Vector3 mousePos = Mouse.current != null ? (Vector3)Mouse.current.position.ReadValue() : Vector3.zero;
#else
            Vector3 mousePos = Input.mousePosition;
#endif
            mousePos.z = visualDepth;
            Vector3 worldPos = Camera.main.ScreenToWorldPoint(mousePos);

            _points.Enqueue(worldPos);
            if (_points.Count > trailLength) _points.Dequeue();

            _lineRenderer.positionCount = _points.Count;
            _lineRenderer.SetPositions(_points.ToArray());
        }

        private void Cut()
        {
#if ENABLE_INPUT_SYSTEM
            Vector3 mousePos = Mouse.current != null ? (Vector3)Mouse.current.position.ReadValue() : Vector3.zero;
#else
            Vector3 mousePos = Input.mousePosition;
#endif
            Ray ray = Camera.main.ScreenPointToRay(mousePos);
            
            RopeController closestRope = null;
            int bestSegmentIndex = -1;
            float bestDistance = float.MaxValue;
            float bestFraction = 0f;

            // Iterate all active ropes using the static registry
            foreach (var rope in RopeController.AllRopes)
            {
                if (rope.Raycast(ray, out float hitDist, out int segIndex, out float segmentFraction))
                {
                    if (hitDist < bestDistance && hitDist < maxDistance)
                    {
                        bestDistance = hitDist;
                        closestRope = rope;
                        bestSegmentIndex = segIndex;
                        bestFraction = segmentFraction;
                    }
                }
            }

            if (closestRope != null)
            {
                closestRope.Split(bestSegmentIndex, bestFraction);
            }
        }
    }
}