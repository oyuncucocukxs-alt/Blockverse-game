using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using Mirror;
using BlockVerse.Network;

namespace BlockVerse.UI
{
    /// <summary>
    /// In-game chat UI. Handles world chat (via Mirror) and global/whisper (via Socket.IO).
    /// </summary>
    public class ChatUI : MonoBehaviour
    {
        [Header("Layout")]
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private Transform messageContainer;
        [SerializeField] private ChatMessageUI messagePrefab;
        [SerializeField] private TMP_InputField chatInput;
        [SerializeField] private Button sendButton;
        [SerializeField] private CanvasGroup canvasGroup;

        [Header("Channel Buttons")]
        [SerializeField] private Button worldChannelBtn;
        [SerializeField] private Button globalChannelBtn;
        [SerializeField] private Button whisperChannelBtn;

        [Header("Settings")]
        [SerializeField] private int maxMessages = 100;
        [SerializeField] private float hideDelay = 8f;

        private ChatChannel _activeChannel = ChatChannel.World;
        private string _whisperTarget;
        private string _whisperTargetName;
        private readonly Queue<ChatMessageUI> _messagePool = new();
        private readonly List<ChatMessageUI> _activeMessages = new();
        private float _lastMessageTime;
        private bool _isFocused;
        private Coroutine _hideCoroutine;

        // ── Channel colors ──
        private static readonly Color WorldColor   = new(0.9f, 0.9f, 0.9f);
        private static readonly Color GlobalColor  = new(0.8f, 1.0f, 0.8f);
        private static readonly Color WhisperColor = new(1.0f, 0.8f, 1.0f);
        private static readonly Color SystemColor  = new(1.0f, 1.0f, 0.5f);
        private static readonly Color ErrorColor   = new(1.0f, 0.4f, 0.4f);

        private void Start()
        {
            // Register network handlers
            NetworkClient.RegisterHandler<ChatMessage>(OnReceiveChatMessage);

            // Socket.IO (global chat / whisper)
            GlobalChatClient.Instance.OnMessageReceived    += OnGlobalMessage;
            GlobalChatClient.Instance.OnWhisperReceived    += OnWhisperMessage;
            GlobalChatClient.Instance.OnPlayerOnline       += OnPlayerOnline;
            GlobalChatClient.Instance.OnPlayerOffline      += OnPlayerOffline;

            // Input
            chatInput.onSubmit.AddListener(OnSendInput);
            sendButton.onClick.AddListener(OnSendClick);
            chatInput.onSelect.AddListener(_ => SetFocused(true));
            chatInput.onDeselect.AddListener(_ => SetFocused(false));

            // Channel buttons
            worldChannelBtn.onClick.AddListener(() => SwitchChannel(ChatChannel.World));
            globalChannelBtn.onClick.AddListener(() => SwitchChannel(ChatChannel.Global));
            whisperChannelBtn.onClick.AddListener(() => SwitchChannel(ChatChannel.Whisper));

            // Start hidden (fade in on activity)
            canvasGroup.alpha = 0.3f;
        }

        // ─────────────────────────────────────────────
        #region Receive Messages

        private void OnReceiveChatMessage(ChatMessage msg)
        {
            Color color = msg.Channel switch
            {
                ChatChannel.World   => WorldColor,
                ChatChannel.Global  => GlobalColor,
                ChatChannel.Whisper => WhisperColor,
                ChatChannel.System  => SystemColor,
                _                  => WorldColor
            };

            AddMessage($"[{msg.SenderName}]: {msg.Text}", color);
        }

        private void OnGlobalMessage(string senderName, string text)
        {
            AddMessage($"🌐 [{senderName}]: {text}", GlobalColor);
        }

        private void OnWhisperMessage(string senderName, string text)
        {
            AddMessage($"💬 [{senderName}] whispers: {text}", WhisperColor);
        }

        private void OnPlayerOnline(string username)
        {
            AddMessage($"⬆ {username} is online", new Color(0.5f, 1f, 0.5f, 0.7f));
        }

        private void OnPlayerOffline(string username)
        {
            AddMessage($"⬇ {username} went offline", new Color(0.7f, 0.7f, 0.7f, 0.5f));
        }

        public void AddSystemMessage(string text)
        {
            AddMessage($"⚙ {text}", SystemColor);
        }

        private void AddMessage(string text, Color color)
        {
            ChatMessageUI msgUI = GetPooledMessage();
            msgUI.Set(text, color);
            msgUI.transform.SetAsLastSibling();
            _activeMessages.Add(msgUI);

            // Trim if over limit
            if (_activeMessages.Count > maxMessages)
            {
                var oldest = _activeMessages[0];
                _activeMessages.RemoveAt(0);
                ReturnToPool(oldest);
            }

            _lastMessageTime = Time.time;

            // Scroll to bottom
            StartCoroutine(ScrollToBottom());

            // Show chat if hidden
            ShowChat();

            // Start hide timer
            if (_hideCoroutine != null) StopCoroutine(_hideCoroutine);
            if (!_isFocused) _hideCoroutine = StartCoroutine(HideAfterDelay());
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Send Messages

        private void OnSendInput(string _) => OnSendClick();

        private void OnSendClick()
        {
            var text = chatInput.text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // Parse commands
            if (text.StartsWith("/"))
            {
                HandleCommand(text);
                chatInput.text = "";
                return;
            }

            SendMessage(text, _activeChannel);
            chatInput.text = "";
            chatInput.ActivateInputField();
        }

        private void SendMessage(string text, ChatChannel channel)
        {
            switch (channel)
            {
                case ChatChannel.World:
                    NetworkClient.Send(new ChatMessage
                    {
                        Text = text,
                        Channel = ChatChannel.World
                    });
                    break;

                case ChatChannel.Global:
                    GlobalChatClient.Instance.SendGlobal(text);
                    break;

                case ChatChannel.Whisper:
                    if (string.IsNullOrEmpty(_whisperTarget))
                    {
                        AddMessage("⚠ No whisper target. Use /w PlayerName first.", ErrorColor);
                        return;
                    }
                    GlobalChatClient.Instance.SendWhisper(_whisperTarget, text);
                    AddMessage($"💬 [You → {_whisperTargetName}]: {text}", WhisperColor);
                    break;
            }
        }

        private void HandleCommand(string raw)
        {
            var parts = raw[1..].Split(' ');
            switch (parts[0].ToLower())
            {
                case "w":
                case "whisper":
                    if (parts.Length >= 3)
                    {
                        _whisperTargetName = parts[1];
                        _whisperTarget = parts[1]; // In production, resolve to playerId
                        var msg = string.Join(" ", parts[2..]);
                        SwitchChannel(ChatChannel.Whisper);
                        SendMessage(msg, ChatChannel.Whisper);
                    }
                    else AddMessage("Usage: /w PlayerName Message", ErrorColor);
                    break;

                case "clear":
                    foreach (var m in _activeMessages) ReturnToPool(m);
                    _activeMessages.Clear();
                    break;

                case "world":
                    SwitchChannel(ChatChannel.World);
                    break;

                case "global":
                case "g":
                    SwitchChannel(ChatChannel.Global);
                    break;

                case "help":
                    AddMessage("/w name msg — whisper | /clear — clear chat | /world /global — switch channel", SystemColor);
                    break;

                default:
                    AddMessage($"Unknown command: {parts[0]}", ErrorColor);
                    break;
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Channel Switching

        private void SwitchChannel(ChatChannel channel)
        {
            _activeChannel = channel;

            // Update button visual states
            void SetActive(Button btn, bool active)
            {
                var colors = btn.colors;
                colors.normalColor = active ? new Color(0.3f, 0.5f, 1f) : new Color(0.2f, 0.2f, 0.2f);
                btn.colors = colors;
            }

            SetActive(worldChannelBtn,   channel == ChatChannel.World);
            SetActive(globalChannelBtn,  channel == ChatChannel.Global);
            SetActive(whisperChannelBtn, channel == ChatChannel.Whisper);

            chatInput.placeholder.GetComponent<TextMeshProUGUI>().text =
                channel switch
                {
                    ChatChannel.World   => "World chat...",
                    ChatChannel.Global  => "Global chat...",
                    ChatChannel.Whisper => $"Whisper to {_whisperTargetName ?? "?"}...",
                    _ => "Chat..."
                };
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Visibility

        private void SetFocused(bool focused)
        {
            _isFocused = focused;
            if (focused)
            {
                ShowChat();
                if (_hideCoroutine != null) StopCoroutine(_hideCoroutine);
            }
            else
            {
                _hideCoroutine = StartCoroutine(HideAfterDelay());
            }
        }

        private void ShowChat()
        {
            canvasGroup.DOFade(1f, 0.2f);
        }

        private IEnumerator HideAfterDelay()
        {
            yield return new WaitForSeconds(hideDelay);
            if (!_isFocused)
                canvasGroup.DOFade(0.3f, 0.5f);
        }

        private IEnumerator ScrollToBottom()
        {
            yield return new WaitForEndOfFrame();
            scrollRect.verticalNormalizedPosition = 0f;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Message Pool

        private ChatMessageUI GetPooledMessage()
        {
            if (_messagePool.Count > 0)
            {
                var m = _messagePool.Dequeue();
                m.gameObject.SetActive(true);
                return m;
            }
            return Instantiate(messagePrefab, messageContainer);
        }

        private void ReturnToPool(ChatMessageUI msg)
        {
            msg.gameObject.SetActive(false);
            msg.transform.SetAsFirstSibling();
            _messagePool.Enqueue(msg);
        }

        #endregion
    }

    public class ChatMessageUI : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI label;

        public void Set(string text, Color color)
        {
            label.text = text;
            label.color = color;

            // Fade-in animation
            var group = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0;
            group.DOFade(1f, 0.2f);
        }
    }
}
