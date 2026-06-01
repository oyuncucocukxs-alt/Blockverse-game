using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlockVerse.UI
{
    // ─────────────────────────────────────────────────────
    // Floating Joystick (dynamic position, snaps to touch)
    // ─────────────────────────────────────────────────────

    public class FloatingJoystick : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [Header("References")]
        [SerializeField] private RectTransform background;
        [SerializeField] private RectTransform handle;

        [Header("Settings")]
        [SerializeField] private float handleRange   = 1f;   // normalized (0-1)
        [SerializeField] private float deadZone      = 0.1f;
        [SerializeField] private bool  floatOnTouch  = true; // repositions to first touch

        private Canvas      _canvas;
        private Camera      _cam;
        private Vector2     _input;
        private bool        _pressing;
        private RectTransform _canvasRect;

        public Vector2 Direction => _input;
        public float   Horizontal => _input.x;
        public float   Vertical   => _input.y;
        public bool    IsPressed  => _pressing;

        private void Start()
        {
            _canvas     = GetComponentInParent<Canvas>();
            _canvasRect = _canvas.GetComponent<RectTransform>();
            _cam        = _canvas.renderMode == RenderMode.ScreenSpaceCamera ? _canvas.worldCamera : null;

            // Hide initially if float mode
            if (floatOnTouch) SetVisible(false);
        }

        public void OnPointerDown(PointerEventData evt)
        {
            _pressing = true;

            if (floatOnTouch)
            {
                // Move background to touch position
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, evt.position, _cam, out Vector2 localPoint);
                background.anchoredPosition = localPoint;
                SetVisible(true);
            }

            OnDrag(evt);
        }

        public void OnDrag(PointerEventData evt)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                background, evt.position, _cam, out Vector2 localPoint)) return;

            Vector2 normalized = localPoint / (background.sizeDelta / 2f);
            float   magnitude  = normalized.magnitude;

            if (magnitude > deadZone)
            {
                if (magnitude > 1f) normalized = normalized.normalized;
                _input = normalized;
            }
            else
            {
                _input = Vector2.zero;
            }

            handle.anchoredPosition = normalized * (background.sizeDelta / 2f) * handleRange;
        }

        public void OnPointerUp(PointerEventData evt)
        {
            _input    = Vector2.zero;
            _pressing = false;
            handle.anchoredPosition = Vector2.zero;
            if (floatOnTouch) SetVisible(false);
        }

        private void SetVisible(bool visible)
        {
            background.gameObject.SetActive(visible);
        }
    }

    // ─────────────────────────────────────────────────────
    // Hold Button (fires continuously while held)
    // ─────────────────────────────────────────────────────

    public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public bool IsHeld { get; private set; }

        public void OnPointerDown(PointerEventData e) => IsHeld = true;
        public void OnPointerUp(PointerEventData e)   => IsHeld = false;
    }

    // ─────────────────────────────────────────────────────
    // Mobile Player Input Bridge
    // Reads floating joystick + touch buttons → feeds PlayerController
    // ─────────────────────────────────────────────────────

    public class MobileInputBridge : MonoBehaviour
    {
        [SerializeField] private FloatingJoystick moveJoystick;
        [SerializeField] private HoldButton       jumpButton;
        [SerializeField] private HoldButton       breakButton;
        [SerializeField] private Button           interactButton;
        [SerializeField] private Button           inventoryButton;
        [SerializeField] private Button           chatButton;
        [SerializeField] private Button           mapButton;

        private Player.PlayerController _player;
        private bool _jumpWasHeld;
        private float _breakTimer;

        private void Start()
        {
            inventoryButton.onClick.AddListener(() => UIManager.Instance.ToggleInventory());
            chatButton.onClick.AddListener(() => UIManager.Instance.ToggleChat());
        }

        private void Update()
        {
            _player = Player.PlayerController.LocalInstance;
            if (_player == null) return;

            // Inject joystick input
            Vector2 dir = moveJoystick.Direction;
            InjectMoveInput(dir);

            // Jump: rising edge detection (not held)
            bool jumpHeld = jumpButton.IsHeld;
            if (jumpHeld && !_jumpWasHeld) InjectJump();
            _jumpWasHeld = jumpHeld;

            // Break block: continuous while held
            if (breakButton.IsHeld)
            {
                _breakTimer += Time.deltaTime;
                InjectBreakHeld();
            }
            else
            {
                if (_breakTimer > 0) InjectBreakReleased();
                _breakTimer = 0;
            }
        }

        // These inject into PlayerController via a shared input state
        private void InjectMoveInput(Vector2 dir) =>
            MobileInputState.MoveInput = dir;

        private void InjectJump() =>
            MobileInputState.JumpPressed = true;

        private void InjectBreakHeld() =>
            MobileInputState.BreakHeld = true;

        private void InjectBreakReleased() =>
            MobileInputState.BreakHeld = false;
    }

    /// <summary>Shared state read by PlayerController on mobile.</summary>
    public static class MobileInputState
    {
        public static Vector2 MoveInput;
        public static bool    JumpPressed;
        public static bool    BreakHeld;
        public static Vector2 AimPosition; // touch position for block targeting

        public static void ConsumeJump() => JumpPressed = false;
    }

    // ─────────────────────────────────────────────────────
    // Screen-space Block Targeting (mobile touch)
    // ─────────────────────────────────────────────────────

    public class MobileBlockTargeting : MonoBehaviour
    {
        [SerializeField] private Camera   mainCamera;
        [SerializeField] private RectTransform targetIndicator; // crosshair UI
        [SerializeField] private float    autoAimRadius = 2f;   // tiles around player

        private Vector2Int _targetTile;

        private void Update()
        {
            if (!Application.isMobilePlatform) return;

            // On mobile: auto-target nearest breakable tile in direction of movement
            var player = Player.PlayerController.LocalInstance;
            if (player == null) return;

            Vector2 moveDir = MobileInputState.MoveInput;

            if (moveDir.magnitude < 0.1f)
            {
                // No movement → aim at tile directly above player (default)
                moveDir = Vector2.up;
            }

            Vector2 origin  = player.transform.position;
            Vector2 aimPos  = origin + moveDir.normalized * autoAimRadius;

            _targetTile = new Vector2Int(
                Mathf.FloorToInt(aimPos.x),
                Mathf.FloorToInt(aimPos.y)
            );

            // Update crosshair world position → screen position
            Vector3 worldPos = new Vector3(_targetTile.x + 0.5f, _targetTile.y + 0.5f, 0);
            Vector2 screenPos = mainCamera.WorldToScreenPoint(worldPos);
            targetIndicator.position = screenPos;

            MobileInputState.AimPosition = aimPos;
        }

        public Vector2Int GetTargetTile() => _targetTile;
    }

    // ─────────────────────────────────────────────────────
    // Touch Swipe Detector (for emote wheel, quick swaps)
    // ─────────────────────────────────────────────────────

    public class SwipeDetector : MonoBehaviour
    {
        private Vector2 _startPos;
        private float   _startTime;
        private const float MIN_DISTANCE = 100f; // pixels
        private const float MAX_TIME     = 0.3f; // seconds

        public event Action<SwipeDirection> OnSwipe;

        private void Update()
        {
            if (Input.touchCount == 0) return;

            var touch = Input.GetTouch(0);
            switch (touch.phase)
            {
                case TouchPhase.Began:
                    _startPos  = touch.position;
                    _startTime = Time.time;
                    break;

                case TouchPhase.Ended:
                    float dist = Vector2.Distance(_startPos, touch.position);
                    float time = Time.time - _startTime;

                    if (dist >= MIN_DISTANCE && time <= MAX_TIME)
                    {
                        Vector2 dir = (touch.position - _startPos).normalized;
                        float   angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

                        SwipeDirection swipe;
                        if      (angle >  45 && angle < 135)  swipe = SwipeDirection.Up;
                        else if (angle < -45 && angle > -135) swipe = SwipeDirection.Down;
                        else if (Mathf.Abs(angle) > 135)      swipe = SwipeDirection.Left;
                        else                                   swipe = SwipeDirection.Right;

                        OnSwipe?.Invoke(swipe);
                    }
                    break;
            }
        }
    }

    public enum SwipeDirection { Up, Down, Left, Right }

    // ─────────────────────────────────────────────────────
    // Safe Area handler (iPhone notch, Android gesture bar)
    // ─────────────────────────────────────────────────────

    [RequireComponent(typeof(RectTransform))]
    public class SafeAreaHandler : MonoBehaviour
    {
        private Rect _lastSafeArea = Rect.zero;

        private void Update()
        {
            var safeArea = Screen.safeArea;
            if (safeArea == _lastSafeArea) return;

            _lastSafeArea = safeArea;
            ApplySafeArea(safeArea);
        }

        private void ApplySafeArea(Rect safe)
        {
            var rt        = GetComponent<RectTransform>();
            var screenSize = new Vector2(Screen.width, Screen.height);

            var anchorMin = safe.position / screenSize;
            var anchorMax = (safe.position + safe.size) / screenSize;

            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
