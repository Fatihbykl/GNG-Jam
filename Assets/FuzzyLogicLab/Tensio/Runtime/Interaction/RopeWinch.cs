using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Tensio
{
    [AddComponentMenu("Tensio/Interaction/Rope Winch")]
    [RequireComponent(typeof(RopeController))]
    public class RopeWinch : MonoBehaviour
    {
        [Header("Winch Settings")]
        [Tooltip("Speed in meters per second to extend/retract.")]
        [SerializeField] private float winchSpeed = 2.0f;
        
        [SerializeField] private float minLength = 0.5f;
        [SerializeField] private float maxLength = 20.0f;

        [Header("Controls")]
        [SerializeField] private KeyCode extendKey = KeyCode.DownArrow;
        [SerializeField] private KeyCode retractKey = KeyCode.UpArrow;

        private RopeController _rope;
        private RopeRenderer _renderer;
        private RopeChainRenderer _chainRenderer;

        private void Start()
        {
            _rope = GetComponent<RopeController>();
            _renderer = GetComponent<RopeRenderer>();
            _chainRenderer = GetComponent<RopeChainRenderer>();
            if (_rope.CurrentLength > maxLength) maxLength = _rope.CurrentLength;
        }

        private void Update()
        {
            float change = 0f;

#if ENABLE_INPUT_SYSTEM
            bool extendPressed = false;
            bool retractPressed = false;
            if (Keyboard.current != null)
            {
                if (extendKey == KeyCode.DownArrow) extendPressed = Keyboard.current.downArrowKey.isPressed;
                else if (extendKey == KeyCode.S) extendPressed = Keyboard.current.sKey.isPressed;
                else extendPressed = Keyboard.current.downArrowKey.isPressed; // Default fallback

                if (retractKey == KeyCode.UpArrow) retractPressed = Keyboard.current.upArrowKey.isPressed;
                else if (retractKey == KeyCode.W) retractPressed = Keyboard.current.wKey.isPressed;
                else retractPressed = Keyboard.current.upArrowKey.isPressed; // Default fallback
            }
#else
            bool extendPressed = Input.GetKey(extendKey);
            bool retractPressed = Input.GetKey(retractKey);
#endif

            if (extendPressed) change += winchSpeed * Time.deltaTime;
            if (retractPressed) change -= winchSpeed * Time.deltaTime;

            if (Mathf.Abs(change) > 0.001f)
            {
                float newLen = Mathf.Clamp(_rope.CurrentLength + change, minLength, maxLength);
                
                float actualChange = newLen - _rope.CurrentLength;
                
                _rope.SetCurrentLength(newLen);
                
                if (_renderer != null)
                {
                    _renderer.UVOffset -= actualChange * _renderer.TextureTiling;
                }

                if (_chainRenderer != null)
                {
                    _chainRenderer.LinkOffset += actualChange;
                }
            }
        }
    }
}