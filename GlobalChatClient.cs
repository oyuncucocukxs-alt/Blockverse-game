using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using SocketIOClient;
using BlockVerse.Core;

namespace BlockVerse
{
    /// <summary>
    /// Unity-side Socket.IO client wrapping the Node.js chat and presence namespaces.
    /// Uses SocketIOClient (NuGet package for Unity).
    /// Thread-safe: callbacks are queued and dispatched on Unity main thread.
    /// </summary>
    public class GlobalChatClient : MonoBehaviour
    {
        public static GlobalChatClient Instance { get; private set; }

        [SerializeField] private AppConfig config;

        private SocketIOUnity _chatSocket;
        private SocketIOUnity _presenceSocket;

        private bool _isConnected;
        private readonly Queue<Action> _mainThreadQueue = new();

        // ── Events (dispatched on main thread) ───────────────────────────────
        public event Action<string, string>         OnMessageReceived;  // senderName, text
        public event Action<string, string>         OnWhisperReceived;  // senderName, text
        public event Action<string>                 OnPlayerOnline;     // username
        public event Action<string>                 OnPlayerOffline;    // username
        public event Action<string, string, string> OnFriendPresence;   // playerId, username, status

        // ─────────────────────────────────────────────
        #region Lifecycle

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            // Drain main-thread callback queue
            lock (_mainThreadQueue)
            {
                while (_mainThreadQueue.Count > 0)
                    _mainThreadQueue.Dequeue()?.Invoke();
            }
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Connect / Disconnect

        public void Connect(string accessToken)
        {
            if (_isConnected) return;

            var uri = new Uri(config.BackendApiUrl.Replace("/v1", ""));

            var options = new SocketIOOptions
            {
                Auth                = new Dictionary<string, string> { { "token", accessToken } },
                Reconnection        = true,
                ReconnectionAttempts = 10,
                ReconnectionDelay   = 2000,
                Transport           = SocketIOClient.Transport.TransportProtocol.WebSocket,
            };

            // Chat namespace
            _chatSocket = new SocketIOUnity($"{uri}/chat", options);
            RegisterChatHandlers();
            _chatSocket.Connect();

            // Presence namespace
            _presenceSocket = new SocketIOUnity($"{uri}/presence", options);
            RegisterPresenceHandlers();
            _presenceSocket.Connect();

            _isConnected = true;
            Debug.Log("[GlobalChat] Connecting to chat server...");
        }

        public void Disconnect()
        {
            _chatSocket?.Disconnect();
            _presenceSocket?.Disconnect();
            _isConnected = false;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Chat Handlers

        private void RegisterChatHandlers()
        {
            _chatSocket.OnConnected += (_, __) =>
                Dispatch(() => Debug.Log("[GlobalChat] Chat connected."));

            _chatSocket.OnDisconnected += (_, __) =>
                Dispatch(() => Debug.Log("[GlobalChat] Chat disconnected."));

            _chatSocket.On("global_message", resp =>
            {
                var msg = resp.GetValue<ChatSocketMessage>();
                Dispatch(() => OnMessageReceived?.Invoke(msg.SenderName, msg.Text));
            });

            _chatSocket.On("whisper_received", resp =>
            {
                var msg = resp.GetValue<ChatSocketMessage>();
                Dispatch(() => OnWhisperReceived?.Invoke(msg.SenderName, msg.Text));
            });

            _chatSocket.On("player_online", resp =>
            {
                var msg = resp.GetValue<PresenceMessage>();
                Dispatch(() => OnPlayerOnline?.Invoke(msg.Username));
            });

            _chatSocket.On("player_offline", resp =>
            {
                var msg = resp.GetValue<PresenceMessage>();
                Dispatch(() => OnPlayerOffline?.Invoke(msg.Username));
            });

            _chatSocket.On("system_message", resp =>
            {
                var msg = resp.GetValue<SystemMessage>();
                Dispatch(() => UI.UIManager.Instance?.ChatUI?.AddSystemMessage(msg.Text));
            });

            _chatSocket.On("chat_history", resp =>
            {
                var msgs = resp.GetValue<List<ChatSocketMessage>>();
                Dispatch(() =>
                {
                    foreach (var m in msgs)
                        OnMessageReceived?.Invoke(m.SenderName, m.Text);
                });
            });

            _chatSocket.OnError += (_, err) =>
                Dispatch(() => Debug.LogError($"[GlobalChat] Error: {err}"));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Presence Handlers

        private void RegisterPresenceHandlers()
        {
            _presenceSocket.OnConnected += (_, __) =>
                Dispatch(() =>
                {
                    Debug.Log("[Presence] Connected.");
                    StartHeartbeat();
                });

            _presenceSocket.On("friend_presence", resp =>
            {
                var msg = resp.GetValue<FriendPresenceMessage>();
                Dispatch(() => OnFriendPresence?.Invoke(msg.PlayerId, msg.Username, msg.Status));
            });
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Send API

        public void SendGlobal(string text)
        {
            if (!_isConnected || string.IsNullOrWhiteSpace(text)) return;
            _chatSocket.Emit("global_chat", new { text });
        }

        public void SendWhisper(string targetPlayerId, string text)
        {
            if (!_isConnected) return;
            _chatSocket.Emit("whisper", new { targetPlayerId, text });
        }

        public void EnterWorld(string worldId)
        {
            _presenceSocket?.Emit("enter_world", new { worldId });
        }

        public void LeaveWorld()
        {
            _presenceSocket?.Emit("leave_world");
        }

        public void GetFriendPresence(List<string> friendIds, Action<Dictionary<string, string>> callback)
        {
            _presenceSocket?.Emit("get_friend_presence", new { friendIds }, resp =>
            {
                var result = resp.GetValue<Dictionary<string, string>>();
                Dispatch(() => callback?.Invoke(result));
            });
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Heartbeat

        private Coroutine _heartbeatCoroutine;

        private void StartHeartbeat()
        {
            if (_heartbeatCoroutine != null) StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = StartCoroutine(HeartbeatLoop());
        }

        private IEnumerator HeartbeatLoop()
        {
            while (_isConnected)
            {
                yield return new WaitForSeconds(30f);
                string worldId = GameManager.Instance?.CurrentState == GameState.InWorld
                    ? GameManager.Instance.LocalPlayer?.LastWorldId
                    : "lobby";

                _chatSocket?.Emit("heartbeat", new { worldId });
                _presenceSocket?.Emit("heartbeat", new { worldId });
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Thread Safety

        private void Dispatch(Action action)
        {
            lock (_mainThreadQueue) _mainThreadQueue.Enqueue(action);
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Socket.IO message DTOs
    // ─────────────────────────────────────────────────────

    [Serializable] class ChatSocketMessage
    {
        [JsonProperty("senderId")]   public string SenderId;
        [JsonProperty("senderName")] public string SenderName;
        [JsonProperty("text")]       public string Text;
        [JsonProperty("channel")]    public int    Channel;
        [JsonProperty("timestamp")]  public long   Timestamp;
    }

    [Serializable] class PresenceMessage
    {
        [JsonProperty("playerId")]  public string PlayerId;
        [JsonProperty("username")] public string Username;
        [JsonProperty("status")]   public string Status;
    }

    [Serializable] class FriendPresenceMessage
    {
        [JsonProperty("playerId")]  public string PlayerId;
        [JsonProperty("username")] public string Username;
        [JsonProperty("status")]   public string Status;
        [JsonProperty("worldId")]  public string WorldId;
    }

    [Serializable] class SystemMessage
    {
        [JsonProperty("text")]      public string Text;
        [JsonProperty("timestamp")] public long   Timestamp;
    }
}
