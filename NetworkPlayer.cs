using System.Collections;
using UnityEngine;
using TMPro;
using DG.Tweening;
using Mirror;
using BlockVerse.Network;

namespace BlockVerse.Player
{
    /// <summary>
    /// Represents another player on the client.
    /// Receives movement relay messages and smoothly interpolates position.
    /// Displays nametag, guild tag, and applies appearance cosmetics.
    /// </summary>
    public class NetworkPlayer : MonoBehaviour
    {
        [Header("Visuals")]
        [SerializeField] private SpriteRenderer  bodyRenderer;
        [SerializeField] private Animator        animator;
        [SerializeField] private TextMeshPro     nameTagText;
        [SerializeField] private TextMeshPro     guildTagText;
        [SerializeField] private SpriteRenderer  chatBubbleRenderer;
        [SerializeField] private TextMeshPro     chatBubbleText;
        [SerializeField] private GameObject      crownIcon;   // world owner indicator
        [SerializeField] private GameObject      adminBadge;
        [SerializeField] private SpriteRenderer  hatLayer;
        [SerializeField] private SpriteRenderer  shirtLayer;
        [SerializeField] private SpriteRenderer  pantsLayer;
        [SerializeField] private SpriteRenderer  shoesLayer;

        [Header("Interpolation")]
        [SerializeField] private float positionSmoothTime = 0.1f;
        [SerializeField] private float maxInterpolationDist = 8f;

        // Identity
        public string PlayerId  { get; private set; }
        public string Username  { get; private set; }
        public bool   IsAdmin   { get; private set; }
        public bool   IsOwner   { get; private set; }

        // Interpolation
        private Vector3  _targetPosition;
        private Vector3  _velocity;
        private bool     _isLocalPlayer;

        // Chat bubble
        private Coroutine _hideChatBubble;

        // Animation hashes
        private static readonly int AnimWalk     = Animator.StringToHash("IsWalking");
        private static readonly int AnimGrounded = Animator.StringToHash("IsGrounded");
        private static readonly int AnimYVel     = Animator.StringToHash("YVelocity");

        // ─────────────────────────────────────────────
        #region Initialization

        /// <summary>Called on server to bind this object to a player connection.</summary>
        public void ServerInitialize(PlayerServerState state)
        {
            // Server-side only: assign ownership for Mirror
            PlayerId = state.PlayerId;
            Username = state.Username;
        }

        /// <summary>Called when this object is spawned for a remote player.</summary>
        public void ClientInitialize(PlayerJoinMessage msg)
        {
            PlayerId = msg.PlayerId;
            Username = msg.Username;

            _targetPosition = new Vector3(msg.Position.x, msg.Position.y, 0);
            transform.position = _targetPosition;

            // Register for movement relay messages
            NetworkClient.RegisterHandler<PlayerMoveRelayMessage>(OnMoveRelay);
            NetworkClient.RegisterHandler<ChatMessage>(OnChatMessage);

            SetupNameTag();
            ApplyAppearance(msg.Appearance);

            // Register in scene player registry
            PlayerRegistry.Register(msg.PlayerId, this);
        }

        private void SetupNameTag()
        {
            nameTagText.text = Username;
            adminBadge.SetActive(IsAdmin);
            crownIcon.SetActive(IsOwner);

            // Nametag always faces camera
            nameTagText.transform.rotation = Quaternion.identity;
        }

        public void ApplyAppearance(AppearanceData appearance)
        {
            void ApplyLayer(SpriteRenderer r, int itemId)
            {
                if (r == null) return;
                var def = ItemDatabase.Instance.GetItem(itemId);
                r.sprite  = def?.WornSprite;
                r.enabled = itemId != 0;
            }

            ApplyLayer(hatLayer,   appearance.HatItemId);
            ApplyLayer(shirtLayer, appearance.ShirtItemId);
            ApplyLayer(pantsLayer, appearance.PantsItemId);
            ApplyLayer(shoesLayer, appearance.ShoeItemId);
        }

        private void OnDestroy()
        {
            if (NetworkClient.active)
            {
                NetworkClient.UnregisterHandler<PlayerMoveRelayMessage>();
            }
            PlayerRegistry.Unregister(PlayerId);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Movement Interpolation

        private void OnMoveRelay(PlayerMoveRelayMessage msg)
        {
            if (msg.PlayerId != PlayerId) return;

            _targetPosition = new Vector3(msg.Position.x, msg.Position.y, 0);
            bodyRenderer.flipX = msg.FlipX;

            // Animate
            bool isWalking = msg.AnimState == 1;
            bool isGrounded = msg.AnimState == 0 || msg.AnimState == 1;
            animator.SetBool(AnimWalk,     isWalking);
            animator.SetBool(AnimGrounded, isGrounded);
            animator.SetFloat(AnimYVel, msg.AnimState == 2 ? 5f :
                                        msg.AnimState == 3 ? -5f : 0f);
        }

        private void Update()
        {
            // Snap if too far (respawn, teleport)
            float dist = Vector3.Distance(transform.position, _targetPosition);
            if (dist > maxInterpolationDist)
            {
                transform.position = _targetPosition;
                return;
            }

            // Smooth interpolation
            transform.position = Vector3.SmoothDamp(
                transform.position, _targetPosition,
                ref _velocity, positionSmoothTime);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Chat Bubble

        private void OnChatMessage(ChatMessage msg)
        {
            if (msg.SenderId != PlayerId) return;
            if (msg.Channel == ChatChannel.Whisper) return;

            ShowChatBubble(msg.Text);
        }

        public void ShowChatBubble(string text)
        {
            chatBubbleRenderer.gameObject.SetActive(true);
            chatBubbleText.text = text.Length > 60 ? text[..60] + "…" : text;

            chatBubbleRenderer.transform
                .DOPunchScale(Vector3.one * 0.1f, 0.2f, 5, 0.5f);

            if (_hideChatBubble != null) StopCoroutine(_hideChatBubble);
            _hideChatBubble = StartCoroutine(HideBubbleAfter(4f));
        }

        private IEnumerator HideBubbleAfter(float delay)
        {
            yield return new WaitForSeconds(delay);
            var cg = chatBubbleRenderer.GetComponent<CanvasGroup>();
            if (cg)
                cg.DOFade(0f, 0.5f)
                    .OnComplete(() => chatBubbleRenderer.gameObject.SetActive(false));
            else
                chatBubbleRenderer.gameObject.SetActive(false);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Click (open profile)

        private void OnMouseDown()
        {
            UIManager.Instance.OpenPlayerProfile(PlayerId);
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Player Registry — fast lookup by ID
    // ─────────────────────────────────────────────────────

    public static class PlayerRegistry
    {
        private static readonly System.Collections.Generic.Dictionary<string, NetworkPlayer>
            _players = new();

        public static void Register(string id, NetworkPlayer p) => _players[id] = p;
        public static void Unregister(string id)                => _players.Remove(id);

        public static NetworkPlayer Get(string id)
        {
            _players.TryGetValue(id, out var p);
            return p;
        }

        public static System.Collections.Generic.IEnumerable<NetworkPlayer> All()
            => _players.Values;

        public static void Clear() => _players.Clear();
    }
}
