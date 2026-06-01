using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using BlockVerse.Network;

namespace BlockVerse.UI
{
    /// <summary>
    /// Player profile panel — shows stats, appearance preview,
    /// trade/friend buttons, and moderation options.
    /// </summary>
    public class PlayerProfileUI : MonoBehaviour
    {
        [Header("Identity")]
        [SerializeField] private TextMeshProUGUI usernameText;
        [SerializeField] private TextMeshProUGUI levelText;
        [SerializeField] private TextMeshProUGUI guildText;
        [SerializeField] private TextMeshProUGUI joinDateText;
        [SerializeField] private TextMeshProUGUI lastSeenText;
        [SerializeField] private Image           onlineDot;

        [Header("Character Preview")]
        [SerializeField] private CharacterPreviewRenderer characterPreview;

        [Header("Stats")]
        [SerializeField] private TextMeshProUGUI playtimeText;
        [SerializeField] private TextMeshProUGUI worldsOwnedText;
        [SerializeField] private TextMeshProUGUI tradesText;

        [Header("Buttons")]
        [SerializeField] private Button addFriendBtn;
        [SerializeField] private Button removeFriendBtn;
        [SerializeField] private Button tradeBtn;
        [SerializeField] private Button whisperBtn;
        [SerializeField] private Button inviteGuildBtn;
        [SerializeField] private Button blockBtn;
        [SerializeField] private Button reportBtn;

        [Header("Admin Buttons (hidden for non-admins)")]
        [SerializeField] private GameObject adminSection;
        [SerializeField] private Button     banBtn;
        [SerializeField] private Button     muteBtn;
        [SerializeField] private Button     kickBtn;

        [Header("Loading")]
        [SerializeField] private GameObject loadingOverlay;
        [SerializeField] private CanvasGroup panelGroup;

        private string _viewedPlayerId;
        private bool   _isFriend;
        private bool   _isBlocked;

        // ─────────────────────────────────────────────
        #region Load Profile

        public void LoadProfile(string playerId)
        {
            _viewedPlayerId = playerId;
            loadingOverlay.SetActive(true);
            panelGroup.alpha = 0;

            StartCoroutine(FetchAndDisplay(playerId));
        }

        private IEnumerator FetchAndDisplay(string playerId)
        {
            PlayerData data = null;
            yield return BackendClient.Instance.GetPlayerData(
                playerId,
                d  => data = d,
                er => UIManager.Instance.ShowError(er)
            );

            loadingOverlay.SetActive(false);

            if (data == null) { gameObject.SetActive(false); yield break; }

            PopulateUI(data);
            panelGroup.DOFade(1f, 0.2f);
        }

        private void PopulateUI(PlayerData data)
        {
            bool isSelf = data.PlayerId == GameManager.Instance.LocalPlayer?.PlayerId;

            usernameText.text  = data.Username;
            levelText.text     = $"Level {data.Level}";
            guildText.text     = string.IsNullOrEmpty(data.GuildId) ? "No Guild" : $"[{data.GuildId}]";
            joinDateText.text  = $"Joined: {FormatDate(data.CreatedAt)}";
            lastSeenText.text  = isSelf ? "You" : "Recently";
            onlineDot.color    = IsPlayerOnline(data.PlayerId)
                ? new Color(0.2f, 1f, 0.3f) : Color.gray;

            // Stats
            worldsOwnedText.text = $"{(data.OwnedWorlds?.Length ?? 0)} worlds";
            playtimeText.text    = FormatPlaytime(data.PlaytimeSeconds);

            // Character preview
            characterPreview?.RenderAppearance(data.Appearance);

            // Button visibility
            bool isAdmin  = GameManager.Instance.LocalPlayer?.IsAdmin ?? false;
            bool inGuild  = !string.IsNullOrEmpty(GameManager.Instance.LocalPlayer?.GuildId);

            addFriendBtn.gameObject.SetActive(!isSelf && !_isFriend);
            removeFriendBtn.gameObject.SetActive(!isSelf && _isFriend);
            tradeBtn.gameObject.SetActive(!isSelf && IsPlayerNearby(data.PlayerId));
            whisperBtn.gameObject.SetActive(!isSelf);
            inviteGuildBtn.gameObject.SetActive(!isSelf && inGuild);
            blockBtn.gameObject.SetActive(!isSelf);
            reportBtn.gameObject.SetActive(!isSelf);
            adminSection.SetActive(isAdmin && !isSelf);

            // Wire buttons
            addFriendBtn.onClick.RemoveAllListeners();
            addFriendBtn.onClick.AddListener(() => OnAddFriend(data.PlayerId, data.Username));

            removeFriendBtn.onClick.RemoveAllListeners();
            removeFriendBtn.onClick.AddListener(() => OnRemoveFriend(data.PlayerId));

            tradeBtn.onClick.RemoveAllListeners();
            tradeBtn.onClick.AddListener(() => OnRequestTrade(data.PlayerId));

            whisperBtn.onClick.RemoveAllListeners();
            whisperBtn.onClick.AddListener(() => OnWhisper(data.Username));

            blockBtn.onClick.RemoveAllListeners();
            blockBtn.onClick.AddListener(() => OnBlock(data.PlayerId));

            reportBtn.onClick.RemoveAllListeners();
            reportBtn.onClick.AddListener(() => OnReport(data.PlayerId, data.Username));

            if (isAdmin)
            {
                banBtn.onClick.RemoveAllListeners();
                banBtn.onClick.AddListener(() => OnAdminBan(data.PlayerId));

                muteBtn.onClick.RemoveAllListeners();
                muteBtn.onClick.AddListener(() => OnAdminMute(data.PlayerId));

                kickBtn.onClick.RemoveAllListeners();
                kickBtn.onClick.AddListener(() => OnAdminKick(data.PlayerId));
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Actions

        private void OnAddFriend(string targetId, string targetName)
        {
            StartCoroutine(BackendClient.Instance.AddFriend(
                targetId,
                () =>
                {
                    _isFriend = true;
                    addFriendBtn.gameObject.SetActive(false);
                    removeFriendBtn.gameObject.SetActive(true);
                    UIManager.Instance.ShowNotification($"Friend request sent to {targetName}!", Color.green);
                },
                err => UIManager.Instance.ShowError(err)
            ));
        }

        private void OnRemoveFriend(string targetId)
        {
            ConfirmDialog.Show("Remove Friend", "Remove this player from your friends list?", "Remove",
                () => StartCoroutine(BackendClient.Instance.RemoveFriend(
                    targetId,
                    () =>
                    {
                        _isFriend = false;
                        addFriendBtn.gameObject.SetActive(true);
                        removeFriendBtn.gameObject.SetActive(false);
                    },
                    err => UIManager.Instance.ShowError(err)
                ))
            );
        }

        private void OnRequestTrade(string targetId)
        {
            NetworkClient.Send(new TradeRequestMessage { TargetPlayerId = targetId });
            UIManager.Instance.CloseActivePanel();
        }

        private void OnWhisper(string username)
        {
            UIManager.Instance.ChatUI.OpenWhisper(username);
            UIManager.Instance.CloseActivePanel();
        }

        private void OnBlock(string targetId)
        {
            ConfirmDialog.Show("Block Player",
                "This player will no longer be able to message you.", "Block",
                () =>
                {
                    _isBlocked = true;
                    UIManager.Instance.ShowNotification("Player blocked.", Color.gray);
                    gameObject.SetActive(false);
                }
            );
        }

        private void OnReport(string targetId, string username)
        {
            ReportDialog.Show(targetId, username);
        }

        // ── Admin ──
        private void OnAdminBan(string targetId)
        {
            InputDialog.Show("Ban Player", "Ban duration (hours, 0 = permanent):", hours =>
            {
                int h = int.TryParse(hours, out int parsed) ? parsed : 24;
                StartCoroutine(BackendClient.Instance.AdminBan(
                    targetId, "Admin ban", h,
                    () => UIManager.Instance.ShowNotification("Player banned.", Color.red),
                    err => UIManager.Instance.ShowError(err)
                ));
            });
        }

        private void OnAdminMute(string targetId)
        {
            StartCoroutine(BackendClient.Instance.AdminMute(
                targetId, 60,
                () => UIManager.Instance.ShowNotification("Player muted for 60 min.", Color.yellow),
                err => UIManager.Instance.ShowError(err)
            ));
        }

        private void OnAdminKick(string targetId)
        {
            StartCoroutine(BackendClient.Instance.AdminKick(
                targetId,
                () => UIManager.Instance.ShowNotification("Kick signal sent.", Color.yellow),
                err => UIManager.Instance.ShowError(err)
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Helpers

        private static bool IsPlayerOnline(string playerId)
        {
            // Check in-world player list
            // In production: query Redis via backend
            return false;
        }

        private static bool IsPlayerNearby(string playerId)
        {
            // Check if player is in same world and close enough to trade
            return GameManager.Instance.CurrentState == GameState.InWorld;
        }

        private static string FormatDate(long unixMs)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
            return dt.ToString("MMM yyyy");
        }

        private static string FormatPlaytime(long seconds)
        {
            if (seconds <= 0) return "New player";
            long h = seconds / 3600;
            long m = (seconds % 3600) / 60;
            if (h > 0) return $"{h}h {m}m";
            return $"{m}m";
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Character Preview Renderer
    // ─────────────────────────────────────────────────────

    public class CharacterPreviewRenderer : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer bodyRenderer;
        [SerializeField] private SpriteRenderer hatRenderer;
        [SerializeField] private SpriteRenderer shirtRenderer;
        [SerializeField] private SpriteRenderer pantsRenderer;
        [SerializeField] private SpriteRenderer shoesRenderer;

        public void RenderAppearance(AppearanceData appearance)
        {
            void ApplyLayer(SpriteRenderer r, int itemId)
            {
                if (itemId == 0) { r.sprite = null; return; }
                var def = ItemDatabase.Instance.GetItem(itemId);
                if (def != null) r.sprite = def.WornSprite;
            }

            ApplyLayer(hatRenderer,   appearance.HatItemId);
            ApplyLayer(shirtRenderer, appearance.ShirtItemId);
            ApplyLayer(pantsRenderer, appearance.PantsItemId);
            ApplyLayer(shoesRenderer, appearance.ShoeItemId);
        }
    }
}
