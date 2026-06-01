using UnityEngine;

namespace BlockVerse.Core
{
    [CreateAssetMenu(fileName = "AppConfig", menuName = "BlockVerse/AppConfig")]
    public class AppConfig : ScriptableObject
    {
        [Header("Server")]
        public string MatchmakerUrl = "https://api.blockverse.io";
        public string BackendApiUrl = "https://api.blockverse.io/v1";
        public int DefaultServerPort = 7777;
        public float NetworkTickRate = 0.05f; // 20 ticks/sec

        [Header("World")]
        public int ChunkWidth = 100;
        public int ChunkHeight = 60;
        public int WorldWidth = 300;   // in chunks
        public int WorldHeight = 6;    // in chunks
        public int ViewDistance = 3;   // chunks around player

        [Header("Block")]
        public float BlockBreakCooldown = 0.1f;
        public float BlockPlaceCooldown = 0.1f;
        public int MaxBlockReach = 5;  // tiles

        [Header("Player")]
        public float MoveSpeed = 5f;
        public float JumpForce = 12f;
        public float Gravity = -25f;
        public int MaxInventorySlots = 36;
        public int HotbarSlots = 9;

        [Header("Farming")]
        public float BaseGrowthTimeSeconds = 3600f; // 1 hour
        public float GrowthSpeedMultiplier = 1f;

        [Header("Economy")]
        public string PrimaryCurrencyId = "gem";
        public string SecondaryCurrencyId = "lock";
        public int TradeWindowSize = 9; // 3x3 trade grid slots

        [Header("Security")]
        public float MaxAllowedSpeed = 8f;       // anti-cheat threshold
        public float TeleportDetectionDist = 10f; // tiles/frame
        public int MaxActionsPerSecond = 20;

        [Header("Performance")]
        public int MaxPlayersPerWorld = 50;
        public int ObjectPoolInitialSize = 100;
        public bool EnableChunkStreaming = true;

        [Header("Firebase")]
        public string FirebaseApiKey;
        public string FirebaseProjectId;
        public string FirebaseAppId;
    }
}
