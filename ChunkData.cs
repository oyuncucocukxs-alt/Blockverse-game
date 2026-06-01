using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BlockVerse.World
{
    [Serializable]
    public class WorldData
    {
        public string WorldId;
        public string Name;
        public string OwnerId;
        public int Width;      // in tiles
        public int Height;     // in tiles
        public int SpawnX;
        public int SpawnY;
        public long CreatedAt;
        public long LastModified;
        public WorldFlags Flags;

        // Access control
        public HashSet<string> BannedPlayers = new();
        public Dictionary<string, BuildPermissionLevel> PlayerPermissions = new();

        // Chunk storage (chunkX,chunkY → ChunkData)
        private Dictionary<Vector2Int, ChunkData> _chunks = new();

        public ChunkData GetChunk(int chunkX, int chunkY)
        {
            _chunks.TryGetValue(new Vector2Int(chunkX, chunkY), out var chunk);
            return chunk;
        }

        public ChunkData GetChunkAt(int worldX, int worldY)
        {
            int cx = worldX / AppConfigRef.ChunkWidth;
            int cy = worldY / AppConfigRef.ChunkHeight;
            return GetChunk(cx, cy);
        }

        public void SetChunk(ChunkData chunk)
        {
            _chunks[new Vector2Int(chunk.ChunkX, chunk.ChunkY)] = chunk;
        }

        public bool HasBuildPermission(string playerId, int worldX, int worldY)
        {
            if (PlayerPermissions.TryGetValue(playerId, out var level))
                return level >= BuildPermissionLevel.Build;
            return !Flags.HasFlag(WorldFlags.Locked);
        }

        public IEnumerable<ChunkData> AllChunks() => _chunks.Values;
    }

    [Flags]
    public enum WorldFlags
    {
        None = 0,
        Locked = 1,       // Only owner/admins can build
        Private = 2,      // Invite only
        NoFighting = 4,   // PvP disabled
        NoFarming = 8,
    }

    public enum BuildPermissionLevel
    {
        None = 0,
        Build = 1,
        Admin = 2,
        Owner = 3
    }

    [Serializable]
    public class ChunkData
    {
        public int ChunkX;
        public int ChunkY;
        public TileData[,] ForegroundTiles;
        public TileData[,] BackgroundTiles;
        public FarmingTileData[,] FarmingTiles;
        public bool IsDirty;
        public long LastSaved;

        private const int WIDTH = 100;
        private const int HEIGHT = 60;

        public ChunkData(int cx, int cy)
        {
            ChunkX = cx;
            ChunkY = cy;
            ForegroundTiles = new TileData[WIDTH, HEIGHT];
            BackgroundTiles = new TileData[WIDTH, HEIGHT];
            FarmingTiles = new FarmingTileData[WIDTH, HEIGHT];
        }

        /// <summary>Binary serialization for network transfer (compact format).</summary>
        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(ChunkX);
            bw.Write(ChunkY);

            // Foreground: run-length encode for efficiency
            WriteLayer(bw, ForegroundTiles);
            WriteLayer(bw, BackgroundTiles);
            WriteFarmingLayer(bw, FarmingTiles);

            return ms.ToArray();
        }

        public static ChunkData Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            int cx = br.ReadInt32();
            int cy = br.ReadInt32();

            var chunk = new ChunkData(cx, cy);
            ReadLayer(br, chunk.ForegroundTiles);
            ReadLayer(br, chunk.BackgroundTiles);
            ReadFarmingLayer(br, chunk.FarmingTiles);

            return chunk;
        }

        private static void WriteLayer(BinaryWriter bw, TileData[,] tiles)
        {
            // Simple run-length encoding on itemId
            int runItemId = tiles[0, 0].ItemId;
            int runHealth = tiles[0, 0].Health;
            int runCount = 1;

            for (int i = 1; i < WIDTH * HEIGHT; i++)
            {
                int x = i % WIDTH;
                int y = i / WIDTH;
                int itemId = tiles[x, y].ItemId;
                int health = tiles[x, y].Health;

                if (itemId == runItemId && health == runHealth && runCount < 255)
                {
                    runCount++;
                }
                else
                {
                    bw.Write((byte)runCount);
                    bw.Write((ushort)runItemId);
                    bw.Write((ushort)runHealth);
                    runItemId = itemId;
                    runHealth = health;
                    runCount = 1;
                }
            }

            bw.Write((byte)runCount);
            bw.Write((ushort)runItemId);
            bw.Write((ushort)runHealth);
        }

        private static void ReadLayer(BinaryReader br, TileData[,] tiles)
        {
            int idx = 0;
            while (idx < WIDTH * HEIGHT)
            {
                byte count = br.ReadByte();
                ushort itemId = br.ReadUInt16();
                ushort health = br.ReadUInt16();

                for (int i = 0; i < count && idx < WIDTH * HEIGHT; i++, idx++)
                {
                    int x = idx % WIDTH;
                    int y = idx / WIDTH;
                    tiles[x, y] = new TileData { ItemId = itemId, Health = health };
                }
            }
        }

        private static void WriteFarmingLayer(BinaryWriter bw, FarmingTileData[,] tiles)
        {
            for (int x = 0; x < WIDTH; x++)
            for (int y = 0; y < HEIGHT; y++)
            {
                var f = tiles[x, y];
                bw.Write(f.SeedItemId);
                bw.Write(f.GrowthStage);
                bw.Write(f.PlantedAt);
            }
        }

        private static void ReadFarmingLayer(BinaryReader br, FarmingTileData[,] tiles)
        {
            for (int x = 0; x < WIDTH; x++)
            for (int y = 0; y < HEIGHT; y++)
            {
                tiles[x, y] = new FarmingTileData
                {
                    SeedItemId = br.ReadInt32(),
                    GrowthStage = br.ReadByte(),
                    PlantedAt = br.ReadInt64()
                };
            }
        }
    }

    [Serializable]
    public struct TileData
    {
        public int ItemId;      // 0 = empty
        public int Health;
        public string PlacedBy; // PlayerId
        public long PlacedAt;   // Unix timestamp

        public static readonly TileData Empty = new() { ItemId = 0, Health = 0 };
        public bool IsEmpty => ItemId == 0;
    }

    [Serializable]
    public struct FarmingTileData
    {
        public int SeedItemId;   // 0 = no seed
        public byte GrowthStage; // 0-7
        public long PlantedAt;   // Unix timestamp
        public bool IsReady => GrowthStage >= 7;
    }

    /// <summary>Static reference shim so ChunkData can access config values without DI.</summary>
    public static class AppConfigRef
    {
        public static int ChunkWidth = 100;
        public static int ChunkHeight = 60;
    }
}
