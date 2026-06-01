using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using Mirror;
using BlockVerse.Core;
using BlockVerse.Network;
using BlockVerse.Inventory;

namespace BlockVerse.Player
{
    /// <summary>
    /// Client-authoritative movement (client predicts, server corrects).
    /// Handles movement, jumping, animations, block interaction input.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(BoxCollider2D))]
    [RequireComponent(typeof(Animator))]
    public class PlayerController : MonoBehaviour
    {
        public static PlayerController LocalInstance { get; private set; }

        [Header("Config")]
        [SerializeField] private AppConfig config;

        [Header("Components")]
        [SerializeField] private Animator animator;
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private Transform nameTagTransform;
        [SerializeField] private GameObject nameTagPrefab;
        [SerializeField] private LayerMask groundLayer;

        private Rigidbody2D _rb;
        private BoxCollider2D _col;
        private bool _isGrounded;
        private bool _jumpRequested;
        private float _coyoteTimer;
        private float _jumpBufferTimer;
        private Vector2 _moveInput;
        private float _networkSendTimer;
        private const float NETWORK_SEND_RATE = 0.05f; // 20hz
        private const float COYOTE_TIME = 0.12f;
        private const float JUMP_BUFFER_TIME = 0.15f;

        // Block interaction
        private Vector2Int _targetTile;
        private bool _isBreaking;
        private float _breakTimer;
        private bool _useBackground;

        // Hotbar selection
        private int _selectedHotbarSlot = 0;

        // Animation hashes
        private static readonly int AnimIsWalking = Animator.StringToHash("IsWalking");
        private static readonly int AnimIsGrounded = Animator.StringToHash("IsGrounded");
        private static readonly int AnimYVelocity = Animator.StringToHash("YVelocity");
        private static readonly int AnimEmote = Animator.StringToHash("Emote");

        // Input actions (Unity Input System)
        private PlayerInputActions _input;

        // ─────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _col = GetComponent<BoxCollider2D>();

            _rb.gravityScale = 0f; // We handle gravity manually
            _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
            _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        }

        public void InitializeAsLocalPlayer(PlayerData data)
        {
            LocalInstance = this;

            _input = new PlayerInputActions();
            _input.Enable();

            _input.Player.Move.performed += ctx => _moveInput = ctx.ReadValue<Vector2>();
            _input.Player.Move.canceled += ctx => _moveInput = Vector2.zero;
            _input.Player.Jump.performed += OnJumpInput;
            _input.Player.Interact.performed += OnInteractInput;
            _input.Player.BreakBlock.performed += ctx => _isBreaking = true;
            _input.Player.BreakBlock.canceled += ctx => { _isBreaking = false; _breakTimer = 0f; };
            _input.Player.ToggleBackground.performed += ctx => _useBackground = !_useBackground;

            for (int i = 0; i < 9; i++)
            {
                int slot = i;
                _input.Player.Hotbar.performed += ctx =>
                {
                    // Read hotbar key (1-9 → 0-8)
                };
            }

            ApplyAppearance(data.Appearance);
        }

        private void OnDestroy()
        {
            _input?.Disable();
            if (LocalInstance == this) LocalInstance = null;
        }

        private void Update()
        {
            if (LocalInstance != this) return;

            HandleGroundCheck();
            HandleCoyoteTime();
            HandleJumpBuffer();
            HandleBlockInteraction();
            HandleHotbarScroll();
            SendNetworkUpdate();
        }

        private void FixedUpdate()
        {
            if (LocalInstance != this) return;
            ApplyMovement();
            ApplyGravity();
            ApplyAnimations();
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Movement

        private void ApplyMovement()
        {
            float targetX = _moveInput.x * config.MoveSpeed;
            _rb.velocity = new Vector2(targetX, _rb.velocity.y);

            // Flip sprite
            if (_moveInput.x < -0.01f) spriteRenderer.flipX = true;
            else if (_moveInput.x > 0.01f) spriteRenderer.flipX = false;

            // Jump execution
            if (_jumpRequested && (_isGrounded || _coyoteTimer > 0))
            {
                _rb.velocity = new Vector2(_rb.velocity.x, config.JumpForce);
                _jumpRequested = false;
                _coyoteTimer = 0f;
                _jumpBufferTimer = 0f;
                AudioManager.Instance.PlaySfx("jump");
            }
        }

        private void ApplyGravity()
        {
            float gravMultiplier = _rb.velocity.y < 0 ? 1.5f : 1f; // Faster fall
            _rb.velocity += Vector2.up * (config.Gravity * gravMultiplier * Time.fixedDeltaTime);
            _rb.velocity = new Vector2(_rb.velocity.x, Mathf.Max(_rb.velocity.y, -30f));
        }

        private void HandleGroundCheck()
        {
            var bounds = _col.bounds;
            _isGrounded = Physics2D.BoxCast(
                bounds.center,
                new Vector2(bounds.size.x * 0.9f, 0.1f),
                0f,
                Vector2.down,
                0.05f,
                groundLayer
            );
        }

        private void HandleCoyoteTime()
        {
            if (_isGrounded)
                _coyoteTimer = COYOTE_TIME;
            else
                _coyoteTimer -= Time.deltaTime;
        }

        private void HandleJumpBuffer()
        {
            if (_jumpBufferTimer > 0)
            {
                _jumpBufferTimer -= Time.deltaTime;
                if (_isGrounded || _coyoteTimer > 0)
                    _jumpRequested = true;
            }
        }

        private void OnJumpInput(InputAction.CallbackContext ctx)
        {
            _jumpBufferTimer = JUMP_BUFFER_TIME;
            _jumpRequested = true;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Animations

        private void ApplyAnimations()
        {
            bool isWalking = Mathf.Abs(_moveInput.x) > 0.1f && _isGrounded;
            animator.SetBool(AnimIsWalking, isWalking);
            animator.SetBool(AnimIsGrounded, _isGrounded);
            animator.SetFloat(AnimYVelocity, _rb.velocity.y);
        }

        public void PlayEmote(int emoteId)
        {
            animator.SetTrigger(AnimEmote);
            animator.SetInteger("EmoteId", emoteId);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Block Interaction

        private void HandleBlockInteraction()
        {
            // Get mouse/touch world position
            Vector2 worldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            int tileX = Mathf.FloorToInt(worldPos.x);
            int tileY = Mathf.FloorToInt(worldPos.y);
            var tile = new Vector2Int(tileX, tileY);

            float dist = Vector2.Distance(transform.position, new Vector2(tileX + 0.5f, tileY + 0.5f));

            if (dist > config.MaxBlockReach) { _isBreaking = false; return; }

            if (tile != _targetTile)
            {
                _targetTile = tile;
                _breakTimer = 0f;
            }

            if (_isBreaking)
            {
                _breakTimer += Time.deltaTime;

                // Visual feedback: update break progress overlay
                UIManager.Instance.BlockBreakHUD.SetProgress(tileX, tileY, _breakTimer);

                // Rate-limited break request
                if (_breakTimer >= config.BlockBreakCooldown)
                {
                    _breakTimer = 0f;
                    NetworkClient.Send(new BlockBreakRequestMessage
                    {
                        X = tileX,
                        Y = tileY,
                        IsBackground = _useBackground
                    });
                }
            }
        }

        private void OnInteractInput(InputAction.CallbackContext ctx)
        {
            Vector2 worldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            int tileX = Mathf.FloorToInt(worldPos.x);
            int tileY = Mathf.FloorToInt(worldPos.y);

            float dist = Vector2.Distance(transform.position, new Vector2(tileX + 0.5f, tileY + 0.5f));
            if (dist > config.MaxBlockReach) return;

            // Get selected item from hotbar
            var selectedItem = InventoryManager.Instance.GetHotbarItem(_selectedHotbarSlot);
            if (selectedItem == null || selectedItem.ItemId == 0) return;

            var itemDef = ItemDatabase.Instance.GetItem(selectedItem.ItemId);
            if (itemDef == null) return;

            if (itemDef.IsPlaceable)
            {
                NetworkClient.Send(new BlockPlaceRequestMessage
                {
                    X = tileX,
                    Y = tileY,
                    IsBackground = _useBackground || itemDef.IsBackgroundOnly,
                    ItemId = selectedItem.ItemId
                });
            }
            else if (itemDef.ItemType == ItemType.Seed)
            {
                NetworkClient.Send(new BlockPlaceRequestMessage
                {
                    X = tileX,
                    Y = tileY,
                    IsBackground = false,
                    ItemId = selectedItem.ItemId
                });
            }
        }

        private void HandleHotbarScroll()
        {
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.1f)
            {
                _selectedHotbarSlot = (_selectedHotbarSlot - (int)Mathf.Sign(scroll) + config.HotbarSlots) % config.HotbarSlots;
                UIManager.Instance.HotbarUI.SelectSlot(_selectedHotbarSlot);
            }

            // Number keys 1-9
            for (int i = 0; i < 9; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                {
                    _selectedHotbarSlot = i;
                    UIManager.Instance.HotbarUI.SelectSlot(i);
                }
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Network Sync

        private void SendNetworkUpdate()
        {
            _networkSendTimer += Time.deltaTime;
            if (_networkSendTimer < NETWORK_SEND_RATE) return;
            _networkSendTimer = 0f;

            byte animState = 0;
            if (!_isGrounded && _rb.velocity.y > 0) animState = 2;
            else if (!_isGrounded && _rb.velocity.y < 0) animState = 3;
            else if (Mathf.Abs(_moveInput.x) > 0.1f) animState = 1;

            NetworkClient.Send(new PlayerMoveMessage
            {
                Position = _rb.position,
                Velocity = _rb.velocity,
                FlipX = spriteRenderer.flipX,
                AnimState = animState,
                ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        public void ApplyServerCorrection(Vector2 correctedPos)
        {
            // Smooth correction to avoid jarring snaps
            StartCoroutine(SmoothCorrect(correctedPos));
        }

        private IEnumerator SmoothCorrect(Vector2 target)
        {
            float t = 0f;
            var startPos = _rb.position;
            while (t < 0.1f)
            {
                t += Time.deltaTime;
                _rb.MovePosition(Vector2.Lerp(startPos, target, t / 0.1f));
                yield return null;
            }
            _rb.MovePosition(target);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Appearance

        public void ApplyAppearance(AppearanceData appearance)
        {
            CharacterRenderer.Apply(spriteRenderer, appearance);
        }

        #endregion
    }
}
