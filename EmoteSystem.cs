using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using Mirror;
using BlockVerse.Network;

namespace BlockVerse.Social
{
    // ─────────────────────────────────────────────────────
    // Network Messages
    // ─────────────────────────────────────────────────────

    public struct EmoteMessage : NetworkMessage
    {
        public string PlayerId;
        public int    EmoteId;
    }

    // ─────────────────────────────────────────────────────
    // Emote Definition
    // ─────────────────────────────────────────────────────

    [Serializable]
    public class EmoteDefinition
    {
        public int    EmoteId;
        public string EmoteName;
        public Sprite Icon;
        public string AnimationTrigger;
        public string ChatText;        // shown in chat bubble (e.g. "*waves*")
        public bool   IsPremium;       // requires purchase
        public int    ShopItemId;
    }

    // ─────────────────────────────────────────────────────
    // Emote Wheel UI
    // ─────────────────────────────────────────────────────

    public class EmoteWheelUI : MonoBehaviour
    {
        [Header("Wheel")]
        [SerializeField] private GameObject     wheelRoot;
        [SerializeField] private EmoteSlotUI[]  slots;       // 8 radial slots
        [SerializeField] private TextMeshProUGUI emoteName;
        [SerializeField] private CanvasGroup    group;

        [Header("Data")]
        [SerializeField] private EmoteDefinition[] allEmotes;
        [SerializeField] private int[] equippedEmoteIds; // player's 8 equipped slots

        [Header("Input")]
        [SerializeField] private KeyCode openKey = KeyCode.Q;

        private bool          _isOpen;
        private EmoteSlotUI   _hoveredSlot;
        private float         _openTime;

        private void Start()
        {
            NetworkClient.RegisterHandler<EmoteMessage>(OnRemoteEmote);
            wheelRoot.SetActive(false);
            BuildWheel();
        }

        private void BuildWheel()
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (i >= equippedEmoteIds.Length) { slots[i].SetEmpty(); continue; }

                int emoteId = equippedEmoteIds[i];
                var def = GetEmoteDef(emoteId);
                if (def == null) { slots[i].SetEmpty(); continue; }

                slots[i].Setup(def, this);

                // Position in circle
                float angle = i * (360f / slots.Length) - 90f;
                float rad   = angle * Mathf.Deg2Rad;
                float radius = 120f;
                slots[i].GetComponent<RectTransform>().anchoredPosition =
                    new Vector2(Mathf.Cos(rad) * radius, Mathf.Sin(rad) * radius);
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(openKey)) OpenWheel();
            if (Input.GetKeyUp(openKey))   CloseWheel();

            if (!_isOpen) return;

            // Detect hover via mouse direction from center
            Vector2 screenCenter = wheelRoot.GetComponent<RectTransform>()
                .TransformPoint(Vector2.zero);
            Vector2 dir = ((Vector2)Input.mousePosition - screenCenter).normalized;

            if (dir.magnitude > 0.1f)
            {
                float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                if (angle < 0) angle += 360f;

                int slotIndex = Mathf.RoundToInt(angle / (360f / slots.Length)) % slots.Length;

                if (_hoveredSlot != null) _hoveredSlot.SetHovered(false);
                _hoveredSlot = slots[slotIndex];
                _hoveredSlot.SetHovered(true);
                emoteName.text = _hoveredSlot.EmoteName;
            }
        }

        private void OpenWheel()
        {
            _isOpen   = true;
            _openTime = Time.time;
            wheelRoot.SetActive(true);
            group.alpha = 0;
            group.DOFade(1f, 0.15f);
            wheelRoot.transform.DOScale(1f, 0.2f).From(0.7f).SetEase(Ease.OutBack);
        }

        private void CloseWheel()
        {
            _isOpen = false;

            // Only fire if wheel was open for at least 0.1s (avoid accidental taps)
            if (_hoveredSlot != null && Time.time - _openTime > 0.1f)
                PlayEmote(_hoveredSlot.EmoteId);

            group.DOFade(0f, 0.1f).OnComplete(() => wheelRoot.SetActive(false));
            if (_hoveredSlot != null) { _hoveredSlot.SetHovered(false); _hoveredSlot = null; }
            emoteName.text = "";
        }

        public void PlayEmote(int emoteId)
        {
            var def = GetEmoteDef(emoteId);
            if (def == null) return;

            // Server request
            NetworkClient.Send(new EmoteMessage
            {
                PlayerId = GameManager.Instance.LocalPlayer?.PlayerId,
                EmoteId  = emoteId
            });

            // Local immediate playback
            ApplyEmote(PlayerController.LocalInstance?.gameObject, def);
        }

        private void OnRemoteEmote(EmoteMessage msg)
        {
            var player = Player.PlayerRegistry.Get(msg.PlayerId);
            if (player == null) return;

            var def = GetEmoteDef(msg.EmoteId);
            if (def != null) ApplyEmote(player.gameObject, def);
        }

        private void ApplyEmote(GameObject target, EmoteDefinition def)
        {
            if (target == null) return;

            var anim = target.GetComponent<Animator>();
            anim?.SetTrigger(def.AnimationTrigger);

            if (!string.IsNullOrEmpty(def.ChatText))
            {
                // Show in chat bubble
                var netPlayer = target.GetComponent<Player.NetworkPlayer>();
                netPlayer?.ShowChatBubble(def.ChatText);
            }

            AudioManager.Instance.PlaySfx($"emote_{def.EmoteId}");
        }

        private EmoteDefinition GetEmoteDef(int emoteId)
        {
            foreach (var e in allEmotes)
                if (e.EmoteId == emoteId) return e;
            return null;
        }
    }

    // ─────────────────────────────────────────────────────
    // Emote Slot UI
    // ─────────────────────────────────────────────────────

    public class EmoteSlotUI : MonoBehaviour
    {
        [SerializeField] private Image           iconImage;
        [SerializeField] private Image           background;
        [SerializeField] private TextMeshProUGUI nameLabel;
        [SerializeField] private Image           lockIcon;
        [SerializeField] private GameObject      premiumBadge;

        private EmoteDefinition _def;
        private EmoteWheelUI    _parent;

        public int    EmoteId   => _def?.EmoteId   ?? -1;
        public string EmoteName => _def?.EmoteName ?? "";

        public void Setup(EmoteDefinition def, EmoteWheelUI parent)
        {
            _def    = def;
            _parent = parent;

            iconImage.sprite    = def.Icon;
            nameLabel.text      = def.EmoteName;
            premiumBadge.SetActive(def.IsPremium);
            lockIcon.gameObject.SetActive(def.IsPremium && !IsUnlocked(def.EmoteId));

            var btn = GetComponent<Button>();
            btn.onClick.AddListener(() => parent.PlayEmote(def.EmoteId));
        }

        public void SetEmpty()
        {
            iconImage.sprite = null;
            nameLabel.text   = "+";
            gameObject.SetActive(true);
        }

        public void SetHovered(bool hovered)
        {
            background.DOColor(
                hovered ? new Color(0.3f, 0.6f, 1f, 0.9f)
                        : new Color(0.1f, 0.1f, 0.1f, 0.7f),
                0.1f
            );
            transform.DOScale(hovered ? 1.15f : 1f, 0.1f);
        }

        private static bool IsUnlocked(int emoteId)
        {
            // Check player's owned emotes
            // In production: compare against player's purchase history
            return emoteId <= 10; // Default emotes are free
        }
    }

    // ─────────────────────────────────────────────────────
    // Server-side Emote Handler (in NetworkGameManager)
    // ─────────────────────────────────────────────────────

    public static class EmoteServerHandler
    {
        public static void HandleEmote(
            Mirror.NetworkConnectionToClient conn,
            EmoteMessage msg,
            System.Collections.Generic.Dictionary<int, PlayerServerState> players)
        {
            if (!players.TryGetValue(conn.connectionId, out var player)) return;

            // Validate emote ID range
            if (msg.EmoteId < 0 || msg.EmoteId > 500) return;

            // Rate limit: max 1 emote per second
            if (!player.ActionRateLimit.Allow()) return;

            // Relay to all players with correct sender ID
            var relay = new EmoteMessage
            {
                PlayerId = player.PlayerId,
                EmoteId  = msg.EmoteId
            };

            NetworkServer.SendToAll(relay);
        }
    }
}
