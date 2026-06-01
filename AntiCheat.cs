using System;
using System.Collections.Generic;
using UnityEngine;

namespace BlockVerse.Security
{
    /// <summary>
    /// Server-side anti-cheat suite.
    /// Validates movement, action rates, inventory checksums, and packet integrity.
    /// </summary>
    public static class AntiCheatLogger
    {
        private static readonly List<AntiCheatEvent> _log = new();
        private static readonly Dictionary<string, ViolationCounter> _violations = new();

        private const int AUTO_BAN_THRESHOLD = 10;  // violations before auto-ban
        private const float VIOLATION_WINDOW = 300f; // 5 minutes

        public static void Log(string playerId, AntiCheatViolation type, string details)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var evt = new AntiCheatEvent
            {
                PlayerId = playerId,
                Violation = type,
                Details = details,
                Timestamp = now
            };

            _log.Add(evt);
            Debug.LogWarning($"[AntiCheat] {playerId}: {type} | {details}");

            // Persist to database
            BackendClient.Instance.LogAntiCheatEvent(evt);

            // Count violations
            if (!_violations.TryGetValue(playerId, out var counter))
            {
                counter = new ViolationCounter();
                _violations[playerId] = counter;
            }

            counter.Add(type, now);

            // Auto-ban check
            if (counter.GetRecentCount(now, VIOLATION_WINDOW) >= AUTO_BAN_THRESHOLD)
            {
                AutoBan(playerId, counter.GetTopViolation());
            }
        }

        private static void AutoBan(string playerId, AntiCheatViolation reason)
        {
            Debug.LogError($"[AntiCheat] AUTO-BAN: {playerId} reason={reason}");
            BackendClient.Instance.AutoBanPlayer(playerId, reason.ToString(), 24 * 60 * 60); // 24h ban
            // The game server will disconnect the player
        }

        public static IReadOnlyList<AntiCheatEvent> GetLog() => _log;
    }

    // ─────────────────────────────────────────────
    // Rate Limiter (token bucket)
    // ─────────────────────────────────────────────

    public class RateLimiter
    {
        private float _tokens;
        private readonly float _maxTokens;
        private readonly float _refillRate; // tokens per second
        private float _lastRefill;

        public RateLimiter(int maxActionsPerSecond)
        {
            _maxTokens = maxActionsPerSecond;
            _tokens = maxActionsPerSecond;
            _refillRate = maxActionsPerSecond;
            _lastRefill = Time.time;
        }

        public bool Allow()
        {
            Refill();
            if (_tokens >= 1f)
            {
                _tokens -= 1f;
                return true;
            }
            return false;
        }

        private void Refill()
        {
            float now = Time.time;
            float delta = now - _lastRefill;
            _tokens = Mathf.Min(_maxTokens, _tokens + delta * _refillRate);
            _lastRefill = now;
        }
    }

    // ─────────────────────────────────────────────
    // Packet Validator
    // ─────────────────────────────────────────────

    public static class PacketValidator
    {
        private static readonly HashSet<int> _validItemIds = new();

        public static void Initialize()
        {
            // Load valid item IDs from database
            foreach (var item in ItemDatabase.Instance.GetAllItems())
                _validItemIds.Add(item.ItemId);
        }

        public static bool ValidateItemId(int itemId)
        {
            return itemId == 0 || _validItemIds.Contains(itemId);
        }

        public static bool ValidateWorldPosition(int x, int y, BlockVerse.World.WorldData world)
        {
            return x >= 0 && x < world.Width && y >= 0 && y < world.Height;
        }

        public static bool ValidateInventorySlot(int slot, int inventorySize)
        {
            return slot >= 0 && slot < inventorySize;
        }

        public static bool ValidateChatMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length > 200) return false;
            // No null bytes or control characters
            foreach (char c in text)
                if (c < 0x20 && c != '\n') return false;
            return true;
        }

        public static bool ValidateItemCount(int count)
        {
            return count >= 1 && count <= 200;
        }
    }

    // ─────────────────────────────────────────────
    // Chat Sanitizer
    // ─────────────────────────────────────────────

    public static class ChatSanitizer
    {
        private static readonly HashSet<string> _bannedWords = new(StringComparer.OrdinalIgnoreCase);

        public static void LoadBannedWords(string[] words)
        {
            _bannedWords.Clear();
            foreach (var w in words)
                _bannedWords.Add(w.Trim());
        }

        public static string Sanitize(string input, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            // Trim and cap length
            var text = input.Trim();
            if (text.Length > maxLength)
                text = text[..maxLength];

            // Remove HTML/script injection
            text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");

            // Filter banned words
            foreach (var word in _bannedWords)
            {
                text = System.Text.RegularExpressions.Regex.Replace(
                    text,
                    System.Text.RegularExpressions.Regex.Escape(word),
                    new string('*', word.Length),
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
            }

            return text;
        }
    }

    // ─────────────────────────────────────────────
    // Inventory Checksum (duplication detection)
    // ─────────────────────────────────────────────

    public static class InventoryChecksum
    {
        /// <summary>
        /// Compute a hash of the inventory state.
        /// Used to detect external modification between snapshots.
        /// </summary>
        public static string Compute(BlockVerse.Inventory.ServerInventory inventory)
        {
            var slots = inventory.Serialize();
            var sb = new System.Text.StringBuilder();
            foreach (var slot in slots)
                sb.Append($"{slot.ItemId}:{slot.Count},");

            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            var hash = sha.ComputeHash(bytes);
            return Convert.ToBase64String(hash)[..12]; // Short checksum
        }
    }

    // ─────────────────────────────────────────────
    // Supporting types
    // ─────────────────────────────────────────────

    public enum AntiCheatViolation
    {
        SpeedHack,
        Teleport,
        BlockReachHack,
        ActionSpam,
        InvalidItem,
        InvalidPosition,
        DuplicationAttempt,
        PacketTampering,
        InventoryTampering
    }

    public class AntiCheatEvent
    {
        public string PlayerId;
        public AntiCheatViolation Violation;
        public string Details;
        public long Timestamp;
    }

    public class ViolationCounter
    {
        private readonly List<(AntiCheatViolation type, float time)> _history = new();

        public void Add(AntiCheatViolation v, float time) => _history.Add((v, time));

        public int GetRecentCount(float now, float window)
        {
            int count = 0;
            foreach (var (_, time) in _history)
                if (now - time < window) count++;
            return count;
        }

        public AntiCheatViolation GetTopViolation()
        {
            var counts = new Dictionary<AntiCheatViolation, int>();
            foreach (var (type, _) in _history)
                counts[type] = counts.GetValueOrDefault(type, 0) + 1;

            AntiCheatViolation top = AntiCheatViolation.PacketTampering;
            int max = 0;
            foreach (var kvp in counts)
                if (kvp.Value > max) { max = kvp.Value; top = kvp.Key; }

            return top;
        }
    }
}
