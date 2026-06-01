using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using BlockVerse.Core;
using BlockVerse.Network;

namespace BlockVerse.UI
{
    /// <summary>
    /// Central UI manager — owns all UI panels and transitions.
    /// Uses DoTween for animations.
    /// </summary>
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        [Header("Panels")]
        [SerializeField] private CanvasGroup loadingScreen;
        [SerializeField] private TextMeshProUGUI loadingText;
        [SerializeField] private Slider loadingProgress;

        [SerializeField] private GameObject mainMenuPanel;
        [SerializeField] private GameObject hudPanel;
        [SerializeField] private GameObject inventoryPanel;
        [SerializeField] private GameObject chatPanel;
        [SerializeField] private GameObject tradePanel;
        [SerializeField] private GameObject shopPanel;
        [SerializeField] private GameObject marketPanel;
        [SerializeField] private GameObject settingsPanel;
        [SerializeField] private GameObject worldSearchPanel;
        [SerializeField] private GameObject playerProfilePanel;
        [SerializeField] private GameObject craftingPanel;
        [SerializeField] private GameObject guildPanel;
        [SerializeField] private GameObject adminPanel;
        [SerializeField] private GameObject errorPopup;
        [SerializeField] private TextMeshProUGUI errorText;

        [Header("HUD Components")]
        public HotbarUI HotbarUI;
        public BlockBreakHUD BlockBreakHUD;
        public ChatUI ChatUI;
        public MinimapUI MinimapUI;
        public PlayerStatusUI PlayerStatusUI;

        [Header("Mobile Controls")]
        [SerializeField] private GameObject mobileControls;
        [SerializeField] private FloatingJoystick moveJoystick;
        [SerializeField] private Button jumpButton;
        [SerializeField] private Button interactButton;

        private GameObject _activePanel;
        private const float FADE_DURATION = 0.25f;

        private void Awake()
        {
            Instance = this;

            // Subscribe to game state changes
            GameManager.Instance.OnGameStateChanged += OnGameStateChanged;

            // Network handlers
            NetworkClient.RegisterHandler<ServerErrorMessage>(OnServerError);

            // Mobile detection
            bool isMobile = Application.isMobilePlatform;
            if (mobileControls) mobileControls.SetActive(isMobile);
        }

        // ─────────────────────────────────────────────
        #region Loading Screen

        public void ShowLoadingScreen(bool show, string message = "Loading...")
        {
            loadingText.text = message;
            loadingProgress.value = 0;

            if (show)
            {
                loadingScreen.gameObject.SetActive(true);
                loadingScreen.DOFade(1f, FADE_DURATION);
            }
            else
            {
                loadingScreen.DOFade(0f, FADE_DURATION)
                    .OnComplete(() => loadingScreen.gameObject.SetActive(false));
            }
        }

        public void SetLoadingProgress(float progress, string message = null)
        {
            loadingProgress.DOValue(progress, 0.1f);
            if (message != null) loadingText.text = message;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Game State → UI Transitions

        private void OnGameStateChanged(GameState state)
        {
            switch (state)
            {
                case GameState.MainMenu:
                    ShowPanel(mainMenuPanel);
                    HideHUD();
                    break;

                case GameState.InWorld:
                    HidePanel(mainMenuPanel);
                    ShowHUD();
                    break;

                case GameState.Paused:
                    ShowPanel(settingsPanel);
                    break;
            }
        }

        private void ShowHUD()
        {
            hudPanel.SetActive(true);
            var group = hudPanel.GetComponent<CanvasGroup>();
            if (group) group.DOFade(1f, FADE_DURATION);

            if (Application.isMobilePlatform && mobileControls)
                mobileControls.SetActive(true);
        }

        private void HideHUD()
        {
            var group = hudPanel.GetComponent<CanvasGroup>();
            if (group)
                group.DOFade(0f, FADE_DURATION).OnComplete(() => hudPanel.SetActive(false));
            else
                hudPanel.SetActive(false);

            if (mobileControls) mobileControls.SetActive(false);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Panel Management

        public void ToggleInventory()
        {
            if (inventoryPanel.activeSelf) HidePanel(inventoryPanel);
            else OpenPanel(inventoryPanel);
        }

        public void ToggleChat()
        {
            if (chatPanel.activeSelf) HidePanel(chatPanel);
            else OpenPanel(chatPanel);
        }

        public void OpenShop()    => OpenPanel(shopPanel);
        public void OpenMarket()  => OpenPanel(marketPanel);
        public void OpenCraft()   => OpenPanel(craftingPanel);
        public void OpenGuild()   => OpenPanel(guildPanel);
        public void OpenSettings() => OpenPanel(settingsPanel);
        public void OpenWorldSearch() => OpenPanel(worldSearchPanel);

        public void OpenPlayerProfile(string playerId)
        {
            playerProfilePanel.GetComponent<PlayerProfileUI>().LoadProfile(playerId);
            OpenPanel(playerProfilePanel);
        }

        private void OpenPanel(GameObject panel)
        {
            panel.SetActive(true);
            var group = panel.GetComponent<CanvasGroup>() ?? panel.AddComponent<CanvasGroup>();
            group.alpha = 0;

            var rt = panel.GetComponent<RectTransform>();
            var originalPos = rt.anchoredPosition;
            rt.anchoredPosition = originalPos + new Vector2(0, -30);

            group.DOFade(1f, FADE_DURATION);
            rt.DOAnchorPos(originalPos, FADE_DURATION).SetEase(Ease.OutBack);

            _activePanel = panel;
        }

        private void HidePanel(GameObject panel)
        {
            var group = panel.GetComponent<CanvasGroup>();
            if (group)
                group.DOFade(0f, FADE_DURATION).OnComplete(() => panel.SetActive(false));
            else
                panel.SetActive(false);
        }

        private void ShowPanel(GameObject panel)
        {
            if (_activePanel != null && _activePanel != panel)
                HidePanel(_activePanel);

            panel.SetActive(true);
            var group = panel.GetComponent<CanvasGroup>() ?? panel.AddComponent<CanvasGroup>();
            group.DOFade(1f, FADE_DURATION);
            _activePanel = panel;
        }

        public void CloseActivePanel()
        {
            if (_activePanel != null) HidePanel(_activePanel);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Error / Notification

        public void ShowError(string message)
        {
            errorText.text = message;
            errorPopup.SetActive(true);

            var group = errorPopup.GetComponent<CanvasGroup>() ?? errorPopup.AddComponent<CanvasGroup>();
            group.DOFade(1f, 0.2f)
                .OnComplete(() =>
                    DOVirtual.DelayedCall(3f, () =>
                        group.DOFade(0f, 0.3f).OnComplete(() => errorPopup.SetActive(false))
                    )
                );
        }

        public void ShowNotification(string message, Color color)
        {
            NotificationPool.Show(message, color);
        }

        private void OnServerError(ServerErrorMessage msg)
        {
            ShowError(msg.Message);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Input Handling

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_activePanel != null && _activePanel.activeSelf)
                    CloseActivePanel();
                else
                    OpenPanel(settingsPanel);
            }

            if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Tab))
                ToggleInventory();

            if (Input.GetKeyDown(KeyCode.Return))
                ToggleChat();
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // HotbarUI
    // ─────────────────────────────────────────────────────

    public class HotbarUI : MonoBehaviour
    {
        [SerializeField] private HotbarSlotUI[] slots;
        [SerializeField] private Image selectionIndicator;
        private int _selectedIndex;

        private void Start()
        {
            InventoryManager.Instance.OnSlotChanged += OnSlotChanged;
            SelectSlot(0);
        }

        private void OnSlotChanged(int index, BlockVerse.Inventory.InventorySlot slot)
        {
            if (index < slots.Length)
                slots[index].Refresh(slot);
        }

        public void SelectSlot(int index)
        {
            _selectedIndex = index;
            var rt = selectionIndicator.rectTransform;
            var targetPos = slots[index].transform.position;
            rt.DOMove(targetPos, 0.1f).SetEase(Ease.OutQuad);

            for (int i = 0; i < slots.Length; i++)
                slots[i].SetSelected(i == index);
        }
    }

    // ─────────────────────────────────────────────────────
    // BlockBreakHUD
    // ─────────────────────────────────────────────────────

    public class BlockBreakHUD : MonoBehaviour
    {
        [SerializeField] private Image progressRing;
        [SerializeField] private GameObject container;
        private Vector2 _currentTile;
        private float _maxTime;

        public void SetProgress(int tileX, int tileY, float elapsed)
        {
            container.SetActive(true);
            var itemDef = BlockVerse.World.WorldEngine_Client.Instance?.GetTileItemDef(tileX, tileY);
            if (itemDef != null)
            {
                _maxTime = itemDef.BreakTime;
                progressRing.fillAmount = Mathf.Clamp01(elapsed / _maxTime);
            }
        }

        public void Hide() => container.SetActive(false);
    }
}
