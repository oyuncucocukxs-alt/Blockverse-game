using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using Mirror;
using BlockVerse.Network;

namespace BlockVerse.Social
{
    // ─────────────────────────────────────────────────────
    // Guild Network Messages
    // ─────────────────────────────────────────────────────

    public struct GuildCreateMessage     : NetworkMessage { public string Name; public string Description; }
    public struct GuildInviteMessage     : NetworkMessage { public string GuildId; public string TargetPlayerId; }
    public struct GuildInviteResponse    : NetworkMessage { public string GuildId; public bool Accepted; }
    public struct GuildKickMessage       : NetworkMessage { public string GuildId; public string TargetPlayerId; }
    public struct GuildPromoteMessage    : NetworkMessage { public string GuildId; public string TargetPlayerId; public GuildRole Role; }
    public struct GuildLeaveMessage      : NetworkMessage { public string GuildId; }
    public struct GuildDisbandMessage    : NetworkMessage { public string GuildId; }
    public struct GuildChatMessage       : NetworkMessage { public string GuildId; public string Text; }
    public struct GuildSyncMessage       : NetworkMessage { public GuildData Data; }

    public enum GuildRole : byte { Member = 0, Officer = 1, Owner = 2 }

    // ─────────────────────────────────────────────────────
    // Guild Data (synced to all members)
    // ─────────────────────────────────────────────────────

    [Serializable]
    public class GuildData
    {
        public string GuildId;
        public string Name;
        public string Description;
        public string OwnerId;
        public int    Level;
        public int    Xp;
        public int    MemberCount;
        public GuildMemberInfo[] Members;
    }

    [Serializable]
    public struct GuildMemberInfo
    {
        public string PlayerId;
        public string Username;
        public GuildRole Role;
        public bool   IsOnline;
        public int    Level;
    }

    // ─────────────────────────────────────────────────────
    // Server-side Guild Manager
    // ─────────────────────────────────────────────────────

    public class GuildManager : MonoBehaviour
    {
        public static GuildManager Instance { get; private set; }

        private readonly Dictionary<string, GuildData>    _guilds     = new();
        private readonly Dictionary<string, string>       _playerGuild = new(); // playerId → guildId
        private readonly Dictionary<string, List<string>> _pendingInvites = new(); // playerId → list of guildIds

        private Dictionary<int, PlayerServerState> _allPlayers;

        private void Awake()
        {
            Instance = this;
            NetworkServer.RegisterHandler<GuildCreateMessage>(OnCreate);
            NetworkServer.RegisterHandler<GuildInviteMessage>(OnInvite);
            NetworkServer.RegisterHandler<GuildInviteResponse>(OnInviteResponse);
            NetworkServer.RegisterHandler<GuildKickMessage>(OnKick);
            NetworkServer.RegisterHandler<GuildPromoteMessage>(OnPromote);
            NetworkServer.RegisterHandler<GuildLeaveMessage>(OnLeave);
            NetworkServer.RegisterHandler<GuildDisbandMessage>(OnDisband);
            NetworkServer.RegisterHandler<GuildChatMessage>(OnGuildChat);
        }

        public void SetPlayerRegistry(Dictionary<int, PlayerServerState> players) => _allPlayers = players;

        // ─── Handlers ──────────────────────────────────────

        private void OnCreate(NetworkConnectionToClient conn, GuildCreateMessage msg)
        {
            var player = GetPlayer(conn);
            if (player == null) return;

            if (_playerGuild.ContainsKey(player.PlayerId))
            {
                SendError(conn, "Already in a guild. Leave first."); return;
            }

            if (string.IsNullOrWhiteSpace(msg.Name) || msg.Name.Length < 3 || msg.Name.Length > 20)
            {
                SendError(conn, "Guild name must be 3-20 characters."); return;
            }

            // Check name uniqueness
            foreach (var g in _guilds.Values)
                if (g.Name.Equals(msg.Name, StringComparison.OrdinalIgnoreCase))
                {
                    SendError(conn, "Guild name already taken."); return;
                }

            // Create via backend
            StartCoroutine(CreateGuildBackend(player, msg.Name, msg.Description));
        }

        private IEnumerator CreateGuildBackend(PlayerServerState founder, string name, string desc)
        {
            string guildId = null;
            yield return BackendClient.Instance.CreateGuild(
                founder.PlayerId, name, desc,
                id => guildId = id,
                err => Debug.LogError($"[Guild] Create error: {err}")
            );

            if (guildId == null) return;

            var guild = new GuildData
            {
                GuildId   = guildId,
                Name      = name,
                Description = desc,
                OwnerId   = founder.PlayerId,
                Level     = 1,
                Xp        = 0,
                Members   = new[]
                {
                    new GuildMemberInfo
                    {
                        PlayerId = founder.PlayerId,
                        Username = founder.Username,
                        Role     = GuildRole.Owner,
                        IsOnline = true,
                        Level    = 1
                    }
                },
                MemberCount = 1
            };

            _guilds[guildId] = guild;
            _playerGuild[founder.PlayerId] = guildId;

            founder.Connection.Send(new GuildSyncMessage { Data = guild });
        }

        private void OnInvite(NetworkConnectionToClient conn, GuildInviteMessage msg)
        {
            var inviter = GetPlayer(conn);
            if (inviter == null) return;

            if (!_playerGuild.TryGetValue(inviter.PlayerId, out var guildId) || guildId != msg.GuildId) return;
            if (!_guilds.TryGetValue(guildId, out var guild)) return;

            // Only officers/owner can invite
            var inviterRole = GetRole(guild, inviter.PlayerId);
            if (inviterRole < GuildRole.Officer)
            {
                SendError(conn, "Only officers and owners can invite."); return;
            }

            // Max members check (default 50)
            if (guild.MemberCount >= 50)
            {
                SendError(conn, "Guild is full (50/50)."); return;
            }

            if (_playerGuild.ContainsKey(msg.TargetPlayerId))
            {
                SendError(conn, "Player is already in a guild."); return;
            }

            // Queue invite for target
            if (!_pendingInvites.ContainsKey(msg.TargetPlayerId))
                _pendingInvites[msg.TargetPlayerId] = new List<string>();
            _pendingInvites[msg.TargetPlayerId].Add(guildId);

            // Notify target if online
            var target = FindPlayerById(msg.TargetPlayerId);
            target?.Connection.Send(new GuildInviteMessage
            {
                GuildId = guildId,
                TargetPlayerId = guild.Name  // Repurpose field to carry guild name to client
            });
        }

        private void OnInviteResponse(NetworkConnectionToClient conn, GuildInviteResponse msg)
        {
            var player = GetPlayer(conn);
            if (player == null) return;

            if (_pendingInvites.TryGetValue(player.PlayerId, out var invites))
                invites.Remove(msg.GuildId);

            if (!msg.Accepted) return;
            if (!_guilds.TryGetValue(msg.GuildId, out var guild)) return;

            // Join guild
            _playerGuild[player.PlayerId] = msg.GuildId;
            guild.MemberCount++;

            var newMember = new GuildMemberInfo
            {
                PlayerId = player.PlayerId,
                Username = player.Username,
                Role     = GuildRole.Member,
                IsOnline = true
            };

            var list = new List<GuildMemberInfo>(guild.Members) { newMember };
            guild.Members = list.ToArray();

            // Save to backend
            StartCoroutine(BackendClient.Instance.AddGuildMember(guild.GuildId, player.PlayerId, null, null));

            // Sync to all online guild members
            BroadcastGuildSync(guild);

            // Welcome message
            BroadcastGuildChat(guild, "System", $"Welcome, {player.Username}! 🎉");
        }

        private void OnKick(NetworkConnectionToClient conn, GuildKickMessage msg)
        {
            var kicker = GetPlayer(conn);
            if (kicker == null) return;

            if (!_guilds.TryGetValue(msg.GuildId, out var guild)) return;

            var kickerRole = GetRole(guild, kicker.PlayerId);
            var targetRole = GetRole(guild, msg.TargetPlayerId);

            if (kickerRole <= targetRole)
            {
                SendError(conn, "Cannot kick a member of equal or higher rank."); return;
            }

            RemoveMemberFromGuild(guild, msg.TargetPlayerId);

            var target = FindPlayerById(msg.TargetPlayerId);
            target?.Connection.Send(new ServerErrorMessage
            {
                Code = ErrorCode.InvalidAction,
                Message = $"You were kicked from {guild.Name}."
            });

            BroadcastGuildSync(guild);
            BroadcastGuildChat(guild, "System", $"{msg.TargetPlayerId} was removed from the guild.");
        }

        private void OnPromote(NetworkConnectionToClient conn, GuildPromoteMessage msg)
        {
            var promoter = GetPlayer(conn);
            if (promoter == null) return;

            if (!_guilds.TryGetValue(msg.GuildId, out var guild)) return;
            if (GetRole(guild, promoter.PlayerId) < GuildRole.Owner)
            {
                SendError(conn, "Only the guild owner can promote members."); return;
            }

            for (int i = 0; i < guild.Members.Length; i++)
            {
                if (guild.Members[i].PlayerId != msg.TargetPlayerId) continue;
                guild.Members[i].Role = msg.Role;
                break;
            }

            StartCoroutine(BackendClient.Instance.UpdateGuildMemberRole(
                guild.GuildId, msg.TargetPlayerId, msg.Role.ToString(), null, null));

            BroadcastGuildSync(guild);
        }

        private void OnLeave(NetworkConnectionToClient conn, GuildLeaveMessage msg)
        {
            var player = GetPlayer(conn);
            if (player == null) return;

            if (!_guilds.TryGetValue(msg.GuildId, out var guild)) return;

            if (guild.OwnerId == player.PlayerId)
            {
                SendError(conn, "Owner cannot leave. Transfer ownership or disband the guild."); return;
            }

            RemoveMemberFromGuild(guild, player.PlayerId);
            BroadcastGuildSync(guild);
            BroadcastGuildChat(guild, "System", $"{player.Username} left the guild.");
        }

        private void OnDisband(NetworkConnectionToClient conn, GuildDisbandMessage msg)
        {
            var player = GetPlayer(conn);
            if (player == null) return;

            if (!_guilds.TryGetValue(msg.GuildId, out var guild)) return;
            if (guild.OwnerId != player.PlayerId)
            {
                SendError(conn, "Only the owner can disband."); return;
            }

            // Notify all members
            foreach (var m in guild.Members)
            {
                _playerGuild.Remove(m.PlayerId);
                var member = FindPlayerById(m.PlayerId);
                member?.Connection.Send(new ServerErrorMessage
                {
                    Code = ErrorCode.InvalidAction,
                    Message = $"Guild '{guild.Name}' was disbanded."
                });
            }

            _guilds.Remove(msg.GuildId);
            StartCoroutine(BackendClient.Instance.DeleteGuild(msg.GuildId, null, null));
        }

        private void OnGuildChat(NetworkConnectionToClient conn, GuildChatMessage msg)
        {
            var player = GetPlayer(conn);
            if (player == null) return;

            if (!_playerGuild.TryGetValue(player.PlayerId, out var guildId)) return;
            if (!_guilds.TryGetValue(guildId, out var guild)) return;

            string sanitized = Security.ChatSanitizer.Sanitize(msg.Text, 200);
            if (string.IsNullOrWhiteSpace(sanitized)) return;

            BroadcastGuildChat(guild, player.Username, sanitized);
        }

        // ─── Helpers ───────────────────────────────────────

        private void RemoveMemberFromGuild(GuildData guild, string playerId)
        {
            _playerGuild.Remove(playerId);
            var list = new List<GuildMemberInfo>(guild.Members);
            list.RemoveAll(m => m.PlayerId == playerId);
            guild.Members = list.ToArray();
            guild.MemberCount = list.Count;
            StartCoroutine(BackendClient.Instance.RemoveGuildMember(guild.GuildId, playerId, null, null));
        }

        private void BroadcastGuildSync(GuildData guild)
        {
            foreach (var m in guild.Members)
            {
                var online = FindPlayerById(m.PlayerId);
                online?.Connection.Send(new GuildSyncMessage { Data = guild });
            }
        }

        private void BroadcastGuildChat(GuildData guild, string senderName, string text)
        {
            var msg = new ChatMessage
            {
                SenderName = senderName,
                Text       = text,
                Channel    = ChatChannel.Guild,
                Timestamp  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            foreach (var m in guild.Members)
                FindPlayerById(m.PlayerId)?.Connection.Send(msg);
        }

        private GuildRole GetRole(GuildData guild, string playerId)
        {
            foreach (var m in guild.Members)
                if (m.PlayerId == playerId) return m.Role;
            return GuildRole.Member;
        }

        private PlayerServerState GetPlayer(NetworkConnectionToClient conn)
        {
            _allPlayers?.TryGetValue(conn.connectionId, out var p);
            return p;
        }

        private PlayerServerState FindPlayerById(string playerId)
        {
            if (_allPlayers == null) return null;
            foreach (var p in _allPlayers.Values)
                if (p.PlayerId == playerId) return p;
            return null;
        }

        private static void SendError(NetworkConnectionToClient conn, string msg) =>
            conn.Send(new ServerErrorMessage { Code = ErrorCode.InvalidAction, Message = msg });
    }

    // ─────────────────────────────────────────────────────
    // Client-side Guild UI
    // ─────────────────────────────────────────────────────

    public class GuildUI : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI guildNameText;
        [SerializeField] private TextMeshProUGUI guildLevelText;
        [SerializeField] private TextMeshProUGUI memberCountText;
        [SerializeField] private Transform memberListContainer;
        [SerializeField] private GuildMemberRowUI memberRowPrefab;
        [SerializeField] private Button createGuildBtn;
        [SerializeField] private Button leaveBtn;
        [SerializeField] private Button disbandBtn;
        [SerializeField] private GameObject noGuildPanel;
        [SerializeField] private GameObject guildPanel;
        [SerializeField] private TMP_InputField inviteField;
        [SerializeField] private Button inviteBtn;

        private GuildData _currentGuild;

        private void Start()
        {
            NetworkClient.RegisterHandler<GuildSyncMessage>(OnGuildSync);
            NetworkClient.RegisterHandler<GuildInviteMessage>(OnInviteReceived);

            createGuildBtn.onClick.AddListener(OpenCreateDialog);
            leaveBtn.onClick.AddListener(LeaveGuild);
            disbandBtn.onClick.AddListener(DisbandGuild);
            inviteBtn.onClick.AddListener(SendInvite);
        }

        private void OnGuildSync(GuildSyncMessage msg)
        {
            _currentGuild = msg.Data;
            RefreshUI();
        }

        private void RefreshUI()
        {
            bool inGuild = _currentGuild != null;
            noGuildPanel.SetActive(!inGuild);
            guildPanel.SetActive(inGuild);
            if (!inGuild) return;

            guildNameText.text   = _currentGuild.Name;
            guildLevelText.text  = $"Lv. {_currentGuild.Level}";
            memberCountText.text = $"{_currentGuild.MemberCount}/50 members";

            foreach (Transform t in memberListContainer) Destroy(t.gameObject);
            foreach (var m in _currentGuild.Members)
            {
                var row = Instantiate(memberRowPrefab, memberListContainer);
                row.Setup(m, _currentGuild, this);
            }

            bool isOwner = _currentGuild.OwnerId == GameManager.Instance.LocalPlayer?.PlayerId;
            disbandBtn.gameObject.SetActive(isOwner);
            leaveBtn.gameObject.SetActive(!isOwner);
        }

        private void OnInviteReceived(GuildInviteMessage msg)
        {
            string guildName = msg.TargetPlayerId; // server repurposes this field
            ConfirmDialog.Show(
                "Guild Invite",
                $"You've been invited to join '{guildName}'. Accept?",
                "Accept",
                () => NetworkClient.Send(new GuildInviteResponse { GuildId = msg.GuildId, Accepted = true }),
                "Decline",
                () => NetworkClient.Send(new GuildInviteResponse { GuildId = msg.GuildId, Accepted = false })
            );
        }

        private void OpenCreateDialog()
        {
            InputDialog.Show("Create Guild", "Guild Name:", name =>
            {
                if (!string.IsNullOrEmpty(name))
                    NetworkClient.Send(new GuildCreateMessage { Name = name, Description = "" });
            });
        }

        private void LeaveGuild()
        {
            if (_currentGuild == null) return;
            ConfirmDialog.Show("Leave Guild", $"Leave '{_currentGuild.Name}'?", "Leave",
                () => NetworkClient.Send(new GuildLeaveMessage { GuildId = _currentGuild.GuildId }));
        }

        private void DisbandGuild()
        {
            if (_currentGuild == null) return;
            ConfirmDialog.Show("Disband Guild",
                $"Permanently disband '{_currentGuild.Name}'? This cannot be undone.", "Disband",
                () => NetworkClient.Send(new GuildDisbandMessage { GuildId = _currentGuild.GuildId }));
        }

        private void SendInvite()
        {
            if (_currentGuild == null || string.IsNullOrEmpty(inviteField.text)) return;
            NetworkClient.Send(new GuildInviteMessage
            {
                GuildId = _currentGuild.GuildId,
                TargetPlayerId = inviteField.text.Trim()
            });
            inviteField.text = "";
        }

        public void KickMember(string playerId)
        {
            if (_currentGuild == null) return;
            NetworkClient.Send(new GuildKickMessage { GuildId = _currentGuild.GuildId, TargetPlayerId = playerId });
        }

        public void PromoteMember(string playerId, GuildRole role)
        {
            if (_currentGuild == null) return;
            NetworkClient.Send(new GuildPromoteMessage
                { GuildId = _currentGuild.GuildId, TargetPlayerId = playerId, Role = role });
        }
    }

    public class GuildMemberRowUI : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI nameText;
        [SerializeField] private TextMeshProUGUI roleText;
        [SerializeField] private Image onlineIndicator;
        [SerializeField] private Button kickBtn;
        [SerializeField] private Button promoteBtn;

        private static readonly Color OnlineColor  = new(0.2f, 1f, 0.2f);
        private static readonly Color OfflineColor = new(0.5f, 0.5f, 0.5f);

        public void Setup(GuildMemberInfo m, GuildData guild, GuildUI ui)
        {
            nameText.text = m.Username;
            roleText.text = m.Role.ToString();
            onlineIndicator.color = m.IsOnline ? OnlineColor : OfflineColor;

            bool isLocalOwner = guild.OwnerId == GameManager.Instance.LocalPlayer?.PlayerId;
            bool isSelf       = m.PlayerId == GameManager.Instance.LocalPlayer?.PlayerId;

            kickBtn.gameObject.SetActive(isLocalOwner && !isSelf);
            promoteBtn.gameObject.SetActive(isLocalOwner && !isSelf && m.Role < GuildRole.Officer);

            kickBtn.onClick.AddListener(() => ui.KickMember(m.PlayerId));
            promoteBtn.onClick.AddListener(() => ui.PromoteMember(m.PlayerId, GuildRole.Officer));
        }
    }
}
