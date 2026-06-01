using System;
using UnityEngine;
using Mirror;
using BlockVerse.Core;
using BlockVerse.Inventory;
using BlockVerse.Security;
using BlockVerse.Network;

namespace BlockVerse
{
    /// <summary>
    /// Authoritative server-side state for a connected player.
    /// Holds position, inventory, rate limiters, and anti-cheat data.
    /// </summary>
    public class PlayerServerState
    {
        // Identity
        public string PlayerId    { get; }
        public string Username    { get; }
        public int    ConnectionId { get; }
        public NetworkConnectionToClient Connection { get; }

        // World state
        public Vector2 Position   { get; set; }
        public bool FlipX         { get; set; }
        public byte AnimState     { get; set; }

        // Permission flags
        public bool IsAdmin       { get; set; }
        public bool IsModerator   { get; set; }
        public bool IsMuted       { get; set; }
        public long MuteExpiry    { get; set; }

        // Appearance
        public AppearanceData Appearance { get; set; }

        // Inventory (server-authoritative)
        public ServerInventory Inventory { get; private set; }

        // Currency (denormalized for fast access)
        private int _gems;
        public int Gems
        {
            get => _gems;
            set => _gems = Mathf.Max(0, value);
        }

        // Rate limiters
        public readonly RateLimiter BlockBreakRateLimit;
        public readonly RateLimiter BlockPlaceRateLimit;
        public readonly RateLimiter ChatRateLimit;
        public readonly RateLimiter ActionRateLimit;

        // Anti-cheat tracking
        private Vector2 _lastValidatedPosition;
        private float   _lastMoveTime;
        private int     _invalidMoveCount;
        private const int MAX_INVALID_MOVES_BEFORE_BAN = 20;

        // Session stats
        public float   SessionStartTime { get; }
        public float   PlaytimeThisSession => Time.time - SessionStartTime;

        private readonly AppConfig _config;

        public PlayerServerState(NetworkConnectionToClient conn, PlayerData data, AppConfig config)
        {
            Connection   = conn;
            ConnectionId = conn.connectionId;
            PlayerId     = data.PlayerId;
            Username     = data.Username;
            IsAdmin      = data.IsAdmin;
            IsModerator  = data.IsModerator;
            IsMuted      = data.IsMuted;
            Appearance   = data.Appearance;
            _gems        = data.Gems;
            _config      = config;
            SessionStartTime = Time.time;

            // Restore inventory from DB data
            Inventory = data.Inventory != null
                ? ServerInventory.Deserialize(data.Inventory)
                : new ServerInventory(config.MaxInventorySlots);

            // Set spawn position
            Position = data.LastPosition != Vector2.zero
                ? data.LastPosition
                : new Vector2(data.SpawnX, data.SpawnY);

            _lastValidatedPosition = Position;
            _lastMoveTime = Time.time;

            // Rate limiters (token bucket)
            BlockBreakRateLimit = new RateLimiter(config.MaxActionsPerSecond);
            BlockPlaceRateLimit = new RateLimiter(config.MaxActionsPerSecond);
            ChatRateLimit       = new RateLimiter(3);  // 3 messages/sec
            ActionRateLimit     = new RateLimiter(config.MaxActionsPerSecond);
        }

        // ─────────────────────────────────────────────
        #region Movement Validation

        public bool ValidateMove(Vector2 newPos)
        {
            float now = Time.time;
            float dt  = now - _lastMoveTime;
            _lastMoveTime = now;

            float dist = Vector2.Distance(_lastValidatedPosition, newPos);

            // Teleport check
            if (dist > _config.TeleportDetectionDist)
            {
                _invalidMoveCount++;
                AntiCheatLogger.Log(PlayerId, AntiCheatViolation.Teleport,
                    $"dist={dist:F1} threshold={_config.TeleportDetectionDist}");
                CheckAutoKick();
                return false;
            }

            // Speed check
            float speed = dt > 0 ? dist / dt : 0;
            if (speed > _config.MaxAllowedSpeed * 1.5f) // 50% buffer
            {
                _invalidMoveCount++;
                AntiCheatLogger.Log(PlayerId, AntiCheatViolation.SpeedHack,
                    $"speed={speed:F1} max={_config.MaxAllowedSpeed}");
                CheckAutoKick();
                return false;
            }

            _lastValidatedPosition = newPos;
            return true;
        }

        private void CheckAutoKick()
        {
            if (_invalidMoveCount >= MAX_INVALID_MOVES_BEFORE_BAN)
            {
                AntiCheatLogger.Log(PlayerId, AntiCheatViolation.PacketTampering,
                    $"Auto-kick after {_invalidMoveCount} violations");
                Connection.Disconnect();
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Tool Power

        public int GetBreakPower(Items.ItemDefinition targetBlock)
        {
            // Check equipped hand item
            var handItem = Inventory.GetEquippedHandItem();
            if (handItem == null) return 10; // Bare hand

            var def = ItemDatabase.Instance.GetItem(handItem.ItemId);
            if (def == null || !def.IsTool) return 10;

            return def.BreakPower;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Mute Check

        public bool CheckAndLiftMute()
        {
            if (!IsMuted) return false;
            if (MuteExpiry > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > MuteExpiry)
            {
                IsMuted = false;
                return false;
            }
            return IsMuted;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Serialization (for DB save)

        public PlayerData ToPlayerData()
        {
            return new PlayerData
            {
                PlayerId     = PlayerId,
                Username     = Username,
                IsAdmin      = IsAdmin,
                IsMuted      = IsMuted,
                Appearance   = Appearance,
                Gems         = Gems,
                Inventory    = Inventory.Serialize(),
                LastPosition = Position,
                PlaytimeAddition = (int)PlaytimeThisSession,
            };
        }

        #endregion
    }

    // ─────────────────────────────────────────────
    // PlayerData (shared DTO between client and server)
    // ─────────────────────────────────────────────

    [Serializable]
    public class PlayerData
    {
        public string PlayerId;
        public string Username;
        public int    Level;
        public int    Xp;
        public int    Gems;
        public int    PremiumCurrency;
        public bool   IsPremium;
        public bool   IsAdmin;
        public bool   IsModerator;
        public bool   IsMuted;
        public AppearanceData Appearance;
        public InventorySlotData[] Inventory;
        public EquipmentSlots Equipment;
        public string LastWorldId;
        public Vector2 LastPosition;
        public int SpawnX, SpawnY;
        public int PlaytimeAddition; // seconds to add this session
        public string[] OwnedWorlds;
        public string GuildId;
    }

    // ─────────────────────────────────────────────
    // ServerInfo (from matchmaker)
    // ─────────────────────────────────────────────

    [Serializable]
    public class ServerInfo
    {
        public string Address;
        public int Port;
        public string WorldId;
        public string ServerId;
    }

    // ─────────────────────────────────────────────
    // Token validation result
    // ─────────────────────────────────────────────

    [Serializable]
    public class TokenValidationResult
    {
        public bool Valid;
        public string PlayerId;
        public string Username;
        public bool IsAdmin;
        public bool IsModerator;
        public string Reason;
    }
}
