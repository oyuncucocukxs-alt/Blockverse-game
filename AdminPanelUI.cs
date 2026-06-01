using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace BlockVerse.UI
{
    /// <summary>
    /// In-game admin panel for moderators.
    /// Shows online players, allows ban/mute/kick/give-item.
    /// Only visible if player.IsAdmin == true.
    /// </summary>
    public class AdminPanelUI : MonoBehaviour
    {
        [Header("Tabs")]
        [SerializeField] private Button onlineTab;
        [SerializeField] private Button searchTab;
        [SerializeField] private Button analyticsTab;
        [SerializeField] private Button chatLogTab;
        [SerializeField] private Button anticheatTab;

        [Header("Online Players Panel")]
        [SerializeField] private Transform    onlineListContainer;
        [SerializeField] private AdminPlayerRow playerRowPrefab;
        [SerializeField] private Button       refreshBtn;

        [Header("Player Search Panel")]
        [SerializeField] private TMP_InputField  searchField;
        [SerializeField] private Button          searchBtn;
        [SerializeField] private Transform       searchResultContainer;
        [SerializeField] private AdminPlayerRow  searchRowPrefab;

        [Header("Analytics Panel")]
        [SerializeField] private TextMeshProUGUI totalPlayersText;
        [SerializeField] private TextMeshProUGUI onlinePlayersText;
        [SerializeField] private TextMeshProUGUI dauText;
        [SerializeField] private TextMeshProUGUI wauText;
        [SerializeField] private TextMeshProUGUI mauText;
        [SerializeField] private TextMeshProUGUI totalWorldsText;
        [SerializeField] private TextMeshProUGUI activeServersText;
        [SerializeField] private TextMeshProUGUI anticheatTodayText;
        [SerializeField] private TextMeshProUGUI newPlayersTodayText;

        [Header("Chat Log Panel")]
        [SerializeField] private TMP_InputField chatFilterWorld;
        [SerializeField] private TMP_InputField chatFilterPlayer;
        [SerializeField] private Button         chatSearchBtn;
        [SerializeField] private Transform      chatLogContainer;
        [SerializeField] private AdminChatRow   chatRowPrefab;

        [Header("AntiCheat Panel")]
        [SerializeField] private Transform    acListContainer;
        [SerializeField] private AdminACRow   acRowPrefab;
        [SerializeField] private Button       acRefreshBtn;

        [Header("Broadcast")]
        [SerializeField] private TMP_InputField broadcastField;
        [SerializeField] private Button         broadcastBtn;

        [Header("Panels")]
        [SerializeField] private GameObject onlinePanel;
        [SerializeField] private GameObject searchPanel;
        [SerializeField] private GameObject analyticsPanel;
        [SerializeField] private GameObject chatLogPanel;
        [SerializeField] private GameObject anticheatPanel;

        [Header("Close")]
        [SerializeField] private Button closeBtn;

        private List<AdminPlayerRow> _onlineRows = new();

        private void Start()
        {
            // Only show for admins
            if (!(GameManager.Instance.LocalPlayer?.IsAdmin ?? false))
            {
                gameObject.SetActive(false);
                return;
            }

            onlineTab.onClick.AddListener(() => SwitchTab(onlinePanel,    LoadOnlinePlayers));
            searchTab.onClick.AddListener(() => SwitchTab(searchPanel,    null));
            analyticsTab.onClick.AddListener(() => SwitchTab(analyticsPanel, LoadAnalytics));
            chatLogTab.onClick.AddListener(() => SwitchTab(chatLogPanel,  null));
            anticheatTab.onClick.AddListener(() => SwitchTab(anticheatPanel, LoadAntiCheat));

            refreshBtn.onClick.AddListener(LoadOnlinePlayers);
            searchBtn.onClick.AddListener(SearchPlayers);
            chatSearchBtn.onClick.AddListener(LoadChatLog);
            acRefreshBtn.onClick.AddListener(LoadAntiCheat);
            broadcastBtn.onClick.AddListener(SendBroadcast);

            closeBtn.onClick.AddListener(() => UIManager.Instance.CloseActivePanel());

            SwitchTab(onlinePanel, LoadOnlinePlayers);
        }

        // ─────────────────────────────────────────────
        #region Tab Switching

        private void SwitchTab(GameObject panel, System.Action onSwitch)
        {
            onlinePanel.SetActive(false);
            searchPanel.SetActive(false);
            analyticsPanel.SetActive(false);
            chatLogPanel.SetActive(false);
            anticheatPanel.SetActive(false);

            panel.SetActive(true);
            onSwitch?.Invoke();
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Online Players

        private void LoadOnlinePlayers()
        {
            foreach (Transform t in onlineListContainer) Destroy(t.gameObject);
            _onlineRows.Clear();

            // Collect from in-world player list
            foreach (var np in Player.PlayerRegistry.All())
            {
                var row = Instantiate(playerRowPrefab, onlineListContainer);
                row.Setup(np.PlayerId, np.Username, np.IsAdmin, this);
                _onlineRows.Add(row);
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Search

        private void SearchPlayers()
        {
            string query = searchField.text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            foreach (Transform t in searchResultContainer) Destroy(t.gameObject);

            StartCoroutine(BackendClient.Instance.AdminSearchPlayers(
                query,
                results =>
                {
                    foreach (var r in results)
                    {
                        var row = Instantiate(searchRowPrefab, searchResultContainer);
                        row.Setup(r.PlayerId, r.Username, r.IsAdmin, this);
                    }
                },
                err => UIManager.Instance.ShowError(err)
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Analytics

        private void LoadAnalytics()
        {
            StartCoroutine(BackendClient.Instance.GetAdminAnalytics(
                data =>
                {
                    totalPlayersText.text  = $"Total Players: {data.Players.Total:N0}";
                    onlinePlayersText.text = $"Online Now: {data.Players.Online:N0}";
                    dauText.text           = $"DAU: {data.Players.Dau:N0}";
                    wauText.text           = $"WAU: {data.Players.Wau:N0}";
                    mauText.text           = $"MAU: {data.Players.Mau:N0}";
                    totalWorldsText.text   = $"Total Worlds: {data.Worlds.Total:N0}";
                    activeServersText.text = $"Active Servers: {data.Worlds.ActiveServers}";
                    anticheatTodayText.text  = $"AC Violations Today: {data.Security.AnticheatViolationsToday}";
                    newPlayersTodayText.text = $"New Players Today: {data.Players.NewToday:N0}";
                },
                err => UIManager.Instance.ShowError(err)
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Chat Log

        private void LoadChatLog()
        {
            foreach (Transform t in chatLogContainer) Destroy(t.gameObject);

            StartCoroutine(BackendClient.Instance.GetAdminChatLogs(
                chatFilterWorld.text.Trim(),
                chatFilterPlayer.text.Trim(),
                logs =>
                {
                    foreach (var log in logs)
                    {
                        var row = Instantiate(chatRowPrefab, chatLogContainer);
                        row.Setup(log.SenderName, log.Text, log.WorldId, log.Timestamp);
                    }
                },
                err => UIManager.Instance.ShowError(err)
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region AntiCheat

        private void LoadAntiCheat()
        {
            foreach (Transform t in acListContainer) Destroy(t.gameObject);

            StartCoroutine(BackendClient.Instance.GetAntiCheatLogs(
                null, // no filter
                logs =>
                {
                    foreach (var log in logs)
                    {
                        var row = Instantiate(acRowPrefab, acListContainer);
                        row.Setup(log.PlayerId, log.Violation, log.Details, log.Timestamp);
                    }
                },
                err => UIManager.Instance.ShowError(err)
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Broadcast

        private void SendBroadcast()
        {
            string msg = broadcastField.text.Trim();
            if (string.IsNullOrEmpty(msg)) return;

            StartCoroutine(BackendClient.Instance.AdminBroadcast(
                msg,
                () =>
                {
                    UIManager.Instance.ShowNotification("Broadcast sent!", Color.green);
                    broadcastField.text = "";
                },
                err => UIManager.Instance.ShowError(err)
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Moderation Actions

        public void BanPlayer(string playerId, string username)
        {
            InputDialog.Show($"Ban {username}", "Duration in hours (0 = permanent):", hours =>
            {
                int h = int.TryParse(hours, out int p) ? p : 24;
                StartCoroutine(BackendClient.Instance.AdminBan(playerId, "Admin ban", h,
                    () => UIManager.Instance.ShowNotification($"{username} banned.", Color.red),
                    err => UIManager.Instance.ShowError(err)));
            });
        }

        public void MutePlayer(string playerId, string username)
        {
            StartCoroutine(BackendClient.Instance.AdminMute(playerId, 60,
                () => UIManager.Instance.ShowNotification($"{username} muted 60 min.", Color.yellow),
                err => UIManager.Instance.ShowError(err)));
        }

        public void KickPlayer(string playerId, string username)
        {
            StartCoroutine(BackendClient.Instance.AdminKick(playerId,
                () => UIManager.Instance.ShowNotification($"Kick signal sent to {username}.", Color.yellow),
                err => UIManager.Instance.ShowError(err)));
        }

        public void GiveItem(string playerId, string username)
        {
            InputDialog.Show($"Give Item to {username}", "Item ID:", itemIdStr =>
            {
                if (!int.TryParse(itemIdStr, out int itemId)) return;
                InputDialog.Show("Count:", "Amount:", countStr =>
                {
                    if (!int.TryParse(countStr, out int count)) count = 1;
                    StartCoroutine(BackendClient.Instance.AdminGiveItem(playerId, itemId, count,
                        () => UIManager.Instance.ShowNotification($"Item given to {username}.", Color.green),
                        err => UIManager.Instance.ShowError(err)));
                });
            });
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Admin Row UIs
    // ─────────────────────────────────────────────────────

    public class AdminPlayerRow : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI nameText;
        [SerializeField] private TextMeshProUGUI idText;
        [SerializeField] private Image           adminBadge;
        [SerializeField] private Button          banBtn;
        [SerializeField] private Button          muteBtn;
        [SerializeField] private Button          kickBtn;
        [SerializeField] private Button          giveBtn;
        [SerializeField] private Button          profileBtn;

        public void Setup(string playerId, string username, bool isAdmin, AdminPanelUI panel)
        {
            nameText.text = username;
            idText.text   = playerId[..8] + "...";
            adminBadge.gameObject.SetActive(isAdmin);

            banBtn.onClick.AddListener(()     => panel.BanPlayer(playerId, username));
            muteBtn.onClick.AddListener(()    => panel.MutePlayer(playerId, username));
            kickBtn.onClick.AddListener(()    => panel.KickPlayer(playerId, username));
            giveBtn.onClick.AddListener(()    => panel.GiveItem(playerId, username));
            profileBtn.onClick.AddListener(() => UIManager.Instance.OpenPlayerProfile(playerId));
        }
    }

    public class AdminChatRow : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI text;
        public void Setup(string sender, string msg, string world, long ts)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
            text.text = $"<color=#aaa>[{dt:HH:mm}]</color> [{world}] <b>{sender}:</b> {msg}";
        }
    }

    public class AdminACRow : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI text;
        public void Setup(string playerId, string violation, string details, System.DateTime ts)
        {
            text.text = $"<color=#f66>[{ts:HH:mm:ss}]</color> <b>{violation}</b> — {playerId[..8]} | {details}";
        }
    }
}
