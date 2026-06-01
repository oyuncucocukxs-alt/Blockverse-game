using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using BlockVerse.Core;
using BlockVerse.Network;
using BlockVerse.World;

namespace BlockVerse.Farming
{
    /// <summary>
    /// Server-side farming manager.
    /// Tracks planted seeds, advances growth stages, triggers harvest events.
    /// </summary>
    public class FarmingManager : MonoBehaviour
    {
        public static FarmingManager Instance { get; private set; }

        [SerializeField] private AppConfig config;

        // Key: world tile position → FarmingEntry
        private readonly Dictionary<Vector2Int, FarmingEntry> _entries = new();

        private const float GROWTH_CHECK_INTERVAL = 60f; // Check every minute
        private const int TOTAL_GROWTH_STAGES = 7;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            StartCoroutine(GrowthCheckLoop());
        }

        // ─────────────────────────────────────────────
        #region Server: Plant Seed

        /// <summary>Called by server when a player places a seed block.</summary>
        public bool PlantSeed(int tileX, int tileY, int seedItemId, string playerId)
        {
            var pos = new Vector2Int(tileX, tileY);
            if (_entries.ContainsKey(pos)) return false;

            var seedDef = ItemDatabase.Instance.GetItem(seedItemId);
            if (seedDef == null || !seedDef.IsSeed) return false;

            var entry = new FarmingEntry
            {
                TileX = tileX,
                TileY = tileY,
                SeedItemId = seedItemId,
                PlantedBy = playerId,
                PlantedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                GrowthStage = 0,
                TotalGrowthTime = seedDef.GrowthTimeSeconds * config.GrowthSpeedMultiplier,
                HarvestDropItemId = seedDef.HarvestDropItemId,
                HarvestDropMin = seedDef.HarvestDropMin,
                HarvestDropMax = seedDef.HarvestDropMax,
                SeedDropCount = seedDef.SeedDropFromHarvest,
                GrowsIntoItemId = seedDef.GrowsIntoItemId
            };

            _entries[pos] = entry;

            // Broadcast initial state
            BroadcastGrowthUpdate(entry);
            return true;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Growth Loop

        private IEnumerator GrowthCheckLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(GROWTH_CHECK_INTERVAL);
                AdvanceGrowth();
            }
        }

        private void AdvanceGrowth()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var toHarvest = new List<Vector2Int>();

            foreach (var kvp in _entries)
            {
                var entry = kvp.Value;
                if (entry.GrowthStage >= TOTAL_GROWTH_STAGES) continue;

                float elapsed = now - entry.PlantedAt;
                float stageTime = entry.TotalGrowthTime / TOTAL_GROWTH_STAGES;
                int newStage = Mathf.Min((int)(elapsed / stageTime), TOTAL_GROWTH_STAGES);

                if (newStage != entry.GrowthStage)
                {
                    entry.GrowthStage = (byte)newStage;
                    BroadcastGrowthUpdate(entry);

                    if (newStage >= TOTAL_GROWTH_STAGES)
                        toHarvest.Add(kvp.Key);
                }
            }
        }

        private void BroadcastGrowthUpdate(FarmingEntry entry)
        {
            NetworkServer.SendToAll(new FarmingGrowthUpdateMessage
            {
                TileX = entry.TileX,
                TileY = entry.TileY,
                GrowthStage = entry.GrowthStage,
                IsReadyToHarvest = entry.GrowthStage >= TOTAL_GROWTH_STAGES
            });
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Harvest

        public HarvestResult Harvest(int tileX, int tileY, string playerId)
        {
            var pos = new Vector2Int(tileX, tileY);
            if (!_entries.TryGetValue(pos, out var entry))
                return HarvestResult.NoPlant;

            if (entry.GrowthStage < TOTAL_GROWTH_STAGES)
                return HarvestResult.NotReady;

            // Calculate drops
            var rng = new System.Random();
            int harvestCount = rng.Next(entry.HarvestDropMin, entry.HarvestDropMax + 1);

            // Rare bonus drop (5% chance for double)
            bool bonusDrop = rng.NextDouble() < 0.05;
            if (bonusDrop) harvestCount *= 2;

            var drops = new List<(int itemId, int count)>
            {
                (entry.HarvestDropItemId, harvestCount),
                (entry.SeedItemId, entry.SeedDropCount) // Seeds returned
            };

            _entries.Remove(pos);

            // Notify world engine to clear farming tile
            WorldEngine_Server.Instance?.ClearFarmingTile(tileX, tileY);

            return new HarvestResult
            {
                Success = true,
                Drops = drops,
                BonusDrop = bonusDrop
            };
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Block Break (destroy plant)

        public void OnFarmingTileDestroyed(int tileX, int tileY)
        {
            var pos = new Vector2Int(tileX, tileY);
            _entries.Remove(pos);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Persistence

        public List<FarmingEntryData> SerializeAll()
        {
            var list = new List<FarmingEntryData>();
            foreach (var entry in _entries.Values)
            {
                list.Add(new FarmingEntryData
                {
                    TileX = entry.TileX,
                    TileY = entry.TileY,
                    SeedItemId = entry.SeedItemId,
                    PlantedBy = entry.PlantedBy,
                    PlantedAt = entry.PlantedAt,
                    GrowthStage = entry.GrowthStage,
                    TotalGrowthTime = entry.TotalGrowthTime
                });
            }
            return list;
        }

        public void LoadFromData(List<FarmingEntryData> data, WorldData worldData)
        {
            _entries.Clear();
            foreach (var d in data)
            {
                var seedDef = ItemDatabase.Instance.GetItem(d.SeedItemId);
                if (seedDef == null) continue;

                // Recalculate growth since server restart
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                float elapsed = now - d.PlantedAt;
                float stageTime = d.TotalGrowthTime / TOTAL_GROWTH_STAGES;
                int currentStage = Mathf.Min((int)(elapsed / stageTime), TOTAL_GROWTH_STAGES);

                var entry = new FarmingEntry
                {
                    TileX = d.TileX,
                    TileY = d.TileY,
                    SeedItemId = d.SeedItemId,
                    PlantedBy = d.PlantedBy,
                    PlantedAt = d.PlantedAt,
                    GrowthStage = (byte)currentStage,
                    TotalGrowthTime = d.TotalGrowthTime,
                    HarvestDropItemId = seedDef.HarvestDropItemId,
                    HarvestDropMin = seedDef.HarvestDropMin,
                    HarvestDropMax = seedDef.HarvestDropMax,
                    SeedDropCount = seedDef.SeedDropFromHarvest,
                    GrowsIntoItemId = seedDef.GrowsIntoItemId
                };

                _entries[new Vector2Int(d.TileX, d.TileY)] = entry;
            }
        }

        #endregion
    }

    // ─────────────────────────────────────────────
    // Supporting types
    // ─────────────────────────────────────────────

    public class FarmingEntry
    {
        public int TileX, TileY;
        public int SeedItemId;
        public string PlantedBy;
        public long PlantedAt;
        public byte GrowthStage;
        public float TotalGrowthTime;
        public int HarvestDropItemId;
        public int HarvestDropMin;
        public int HarvestDropMax;
        public int SeedDropCount;
        public int GrowsIntoItemId;
    }

    [Serializable]
    public class FarmingEntryData
    {
        public int TileX, TileY;
        public int SeedItemId;
        public string PlantedBy;
        public long PlantedAt;
        public byte GrowthStage;
        public float TotalGrowthTime;
    }

    public class HarvestResult
    {
        public bool Success;
        public bool BonusDrop;
        public List<(int itemId, int count)> Drops = new();

        public static readonly HarvestResult NoPlant = new() { Success = false };
        public static readonly HarvestResult NotReady = new() { Success = false };
    }
}
