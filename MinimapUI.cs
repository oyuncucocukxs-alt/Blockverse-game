using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace BlockVerse.UI
{
    /// <summary>
    /// Real-time minimap rendered to a RenderTexture.
    /// Shows terrain, player position, other players, and POIs.
    /// Uses a small orthographic camera that follows the local player.
    /// </summary>
    public class MinimapUI : MonoBehaviour
    {
        [Header("Minimap Camera")]
        [SerializeField] private Camera    minimapCamera;
        [SerializeField] private float     mapSize = 50f;       // world units covered
        [SerializeField] private LayerMask renderLayers;

        [Header("UI")]
        [SerializeField] private RawImage    minimapImage;
        [SerializeField] private RectTransform playerDot;
        [SerializeField] private Transform   playerDotContainer;

        [Header("Icons")]
        [SerializeField] private GameObject otherPlayerDotPrefab;
        [SerializeField] private Sprite     playerIcon;
        [SerializeField] private Sprite     vendingIcon;
        [SerializeField] private Sprite     portalIcon;

        [Header("Controls")]
        [SerializeField] private Button     zoomInBtn;
        [SerializeField] private Button     zoomOutBtn;
        [SerializeField] private TextMeshProUGUI coordText;
        [SerializeField] private Button     toggleBtn;

        private RenderTexture _renderTex;
        private bool          _isVisible = true;
        private float         _currentZoom;
        private const float   MIN_ZOOM = 20f;
        private const float   MAX_ZOOM = 200f;
        private float         _updateTimer;
        private const float   UPDATE_INTERVAL = 0.2f; // update at 5Hz

        // ─────────────────────────────────────────────
        #region Init

        private void Start()
        {
            _currentZoom = mapSize;

            // Create render texture
            _renderTex = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGB32);
            _renderTex.antiAliasing = 1;
            minimapCamera.targetTexture = _renderTex;
            minimapCamera.orthographic  = true;
            minimapCamera.cullingMask   = renderLayers;
            minimapImage.texture = _renderTex;

            zoomInBtn.onClick.AddListener(() => Zoom(-10f));
            zoomOutBtn.onClick.AddListener(() => Zoom(+10f));
            toggleBtn.onClick.AddListener(Toggle);
        }

        private void OnDestroy()
        {
            if (_renderTex != null)
            {
                _renderTex.Release();
                Destroy(_renderTex);
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Update

        private void Update()
        {
            var player = Player.PlayerController.LocalInstance;
            if (player == null || !_isVisible) return;

            // Move minimap camera to follow player
            var playerPos = player.transform.position;
            minimapCamera.transform.position = new Vector3(playerPos.x, playerPos.y, -10f);
            minimapCamera.orthographicSize   = _currentZoom;

            // Update coord display
            _updateTimer += Time.deltaTime;
            if (_updateTimer >= UPDATE_INTERVAL)
            {
                _updateTimer = 0;
                int tx = Mathf.FloorToInt(playerPos.x);
                int ty = Mathf.FloorToInt(playerPos.y);
                coordText.text = $"{tx}, {ty}";
                UpdateOtherPlayerDots();
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Player Dots

        private void UpdateOtherPlayerDots()
        {
            // Clear old dots
            foreach (Transform t in playerDotContainer)
                if (t != playerDot) Destroy(t.gameObject);

            var localPos = Player.PlayerController.LocalInstance?.transform.position ?? Vector3.zero;
            float mapHalfSize = _currentZoom;

            foreach (var np in Player.PlayerRegistry.All())
            {
                if (np.PlayerId == GameManager.Instance.LocalPlayer?.PlayerId) continue;

                // Convert world pos → minimap UV
                Vector2 delta = (Vector2)np.transform.position - (Vector2)localPos;
                float u = (delta.x / (mapHalfSize * 2f)) + 0.5f;
                float v = (delta.y / (mapHalfSize * 2f)) + 0.5f;

                if (u < 0 || u > 1 || v < 0 || v > 1) continue; // off-map

                var dot = Instantiate(otherPlayerDotPrefab, playerDotContainer);
                var rt  = dot.GetComponent<RectTransform>();
                var mapRect = minimapImage.rectTransform.rect;

                rt.anchoredPosition = new Vector2(
                    (u - 0.5f) * mapRect.width,
                    (v - 0.5f) * mapRect.height
                );
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Controls

        private void Zoom(float delta)
        {
            _currentZoom = Mathf.Clamp(_currentZoom + delta, MIN_ZOOM, MAX_ZOOM);
        }

        private void Toggle()
        {
            _isVisible = !_isVisible;
            minimapImage.gameObject.SetActive(_isVisible);
            minimapCamera.gameObject.SetActive(_isVisible);
            playerDotContainer.gameObject.SetActive(_isVisible);
            coordText.gameObject.SetActive(_isVisible);
            zoomInBtn.gameObject.SetActive(_isVisible);
            zoomOutBtn.gameObject.SetActive(_isVisible);
        }

        #endregion
    }
}
