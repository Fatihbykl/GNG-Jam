using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Tensio.Demos
{
    [AddComponentMenu("Tensio/Demo/FPS Controller")]
    [RequireComponent(typeof(Rigidbody))]
    public class FPSController : MonoBehaviour
    {
        [Header("Movement Settings")]
        public float moveSpeed = 4500f;
        public float maxSpeed = 8f;
        public float jumpForce = 550f;
        public float airMultiplier = 0.4f;
    
        [Header("Drag Settings")]
        public float groundDrag = 6f;
        public float airDrag = 1f;

        [Header("Mouse Look Settings")]
        public Transform playerCamera;
        public float sensitivityX = 2f;
        public float sensitivityY = 2f;
        public float lookXLimit = 85f;

        [Header("Ground Detection")]
        public LayerMask groundLayer;
        public Transform groundCheck;
        public float groundDistance = 0.4f;

        // --- State ---
        private bool _isGrounded;
        private Rigidbody _rb;
        private float _rotationX = 0f;
        private Vector2 _input;
    
        public bool isGrappling = false; 

        void Start()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.freezeRotation = true;
        
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void Update()
        {
            _isGrounded = Physics.CheckSphere(groundCheck.position, groundDistance, groundLayer);

            HandleMouseLook();

#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
            {
                float h = 0f;
                if (Keyboard.current.dKey.isPressed) h += 1f;
                if (Keyboard.current.aKey.isPressed) h -= 1f;
                if (Keyboard.current.rightArrowKey.isPressed) h += 1f;
                if (Keyboard.current.leftArrowKey.isPressed) h -= 1f;
                _input.x = Mathf.Clamp(h, -1f, 1f);

                float v = 0f;
                if (Keyboard.current.wKey.isPressed) v += 1f;
                if (Keyboard.current.sKey.isPressed) v -= 1f;
                if (Keyboard.current.upArrowKey.isPressed) v += 1f;
                if (Keyboard.current.downArrowKey.isPressed) v -= 1f;
                _input.y = Mathf.Clamp(v, -1f, 1f);
            }
            bool jumpPressed = Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            _input.x = Input.GetAxisRaw("Horizontal");
            _input.y = Input.GetAxisRaw("Vertical");
            bool jumpPressed = Input.GetKeyDown(KeyCode.Space);
#endif

            if (jumpPressed && _isGrounded && !isGrappling)
            {
                Jump();
            }

            if (isGrappling)
            {
#if UNITY_6000_0_OR_NEWER
                _rb.linearDamping = 0.5f;
#else
                _rb.drag = 0.5f;
#endif
            }
            else
            {
#if UNITY_6000_0_OR_NEWER
                _rb.linearDamping = _isGrounded ? groundDrag : airDrag;
#else
                _rb.drag = _isGrounded ? groundDrag : airDrag;
#endif
            }
            
            if (!isGrappling)
            {
                LimitSpeed();
            }
        }

        void FixedUpdate()
        {
            if (!isGrappling)
            {
                MovePlayer();
            }
        }

        void HandleMouseLook()
        {
#if ENABLE_INPUT_SYSTEM
            float mouseX = Mouse.current != null ? Mouse.current.delta.ReadValue().x * 0.1f * sensitivityX : 0f;
            float mouseY = Mouse.current != null ? Mouse.current.delta.ReadValue().y * 0.1f * sensitivityY : 0f;
#else
            float mouseX = Input.GetAxis("Mouse X") * sensitivityX;
            float mouseY = Input.GetAxis("Mouse Y") * sensitivityY;
#endif

            _rotationX -= mouseY;
            _rotationX = Mathf.Clamp(_rotationX, -lookXLimit, lookXLimit);
            playerCamera.localRotation = Quaternion.Euler(_rotationX, 0, 0);

            transform.rotation *= Quaternion.Euler(0, mouseX, 0);
        }

        void MovePlayer()
        {
            Vector3 moveDir = transform.forward * _input.y + transform.right * _input.x;
            moveDir.Normalize();

            if (_isGrounded)
            {
                _rb.AddForce(moveDir * moveSpeed * Time.fixedDeltaTime, ForceMode.Force);
            }
            else
            {
                _rb.AddForce(moveDir * moveSpeed * airMultiplier * Time.fixedDeltaTime, ForceMode.Force);
            }
        }

        void LimitSpeed()
        {
#if UNITY_6000_0_OR_NEWER
            Vector3 flatVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
#else
            Vector3 flatVel = new Vector3(_rb.velocity.x, 0f, _rb.velocity.z);
#endif

            if (flatVel.magnitude > maxSpeed)
            {
                Vector3 limitedVel = flatVel.normalized * maxSpeed;
#if UNITY_6000_0_OR_NEWER
                _rb.linearVelocity = new Vector3(limitedVel.x, _rb.linearVelocity.y, limitedVel.z);
#else
                _rb.velocity = new Vector3(limitedVel.x, _rb.velocity.y, limitedVel.z);
#endif
            }
        }

        void Jump()
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
#else
            _rb.velocity = new Vector3(_rb.velocity.x, 0f, _rb.velocity.z);
#endif
            _rb.AddForce(transform.up * jumpForce, ForceMode.Impulse);
        }

        public void SetGrapplingStatus(bool active)
        {
            isGrappling = active;
        }
    }
}