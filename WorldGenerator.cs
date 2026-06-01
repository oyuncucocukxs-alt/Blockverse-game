using System;
using UnityEngine;
using BlockVerse.Core;

namespace BlockVerse.World
{
    /// <summary>
    /// Generates a new world using layered Perlin noise with biome support.
    /// Runs server-side only.
    /// </summary>
    public static class WorldGenerator
    {
        private const int DIRT_DEPTH = 4;
        private const int STONE_START = 20;
        private const int BEDROCK_Y = 2;
        private const int CAVE_THRESHOLD_OFFSET = 6;

        // Item IDs (must match ItemDatabase)
        private const int ID_BEDROCK   = 1;
        private const int ID_DIRT      = 2;
        private const int ID_STONE     = 3;
        private const int ID_GRASS     = 4;
        private const int ID_SAND      = 5;
        private const int ID_LAVA      = 6;
        private const int ID_WATER     = 7;
        private const int ID_CAVE_DIRT = 8;
        private const int ID_WOOD      = 9;
        private const int ID_LEAVES    = 10;
        private const int ID_BG_STONE  = 101;
        private const int ID_BG_DIRT   = 102;
        private const int ID_BG_SKY    = 103;

        public static WorldData GenerateWorld(string worldId, AppConfig config)
        {
            int seed = GetSeed(worldId);
            var rng = new System.Random(seed);

            int totalWidth  = config.WorldWidth * config.ChunkWidth;
            int totalHeight = config.WorldHeight * config.ChunkHeight;

            var world = new WorldData
            {
                WorldId = worldId,
                Name = worldId,
                Width = totalWidth,
                Height = totalHeight,
                SpawnX = totalWidth / 2,
                SpawnY = GetSurfaceY(totalWidth / 2, totalHeight, seed) + 2,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastModified = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            // Generate chunk by chunk
            for (int cx = 0; cx < config.WorldWidth; cx++)
            {
                for (int cy = 0; cy < config.WorldHeight; cy++)
                {
                    var chunk = GenerateChunk(cx, cy, config, seed, rng);
                    world.SetChunk(chunk);
                }
            }

            return world;
        }

        private static ChunkData GenerateChunk(int chunkX, int chunkY, AppConfig config, int seed, System.Random rng)
        {
            var chunk = new ChunkData(chunkX, chunkY);
            int totalHeight = config.WorldHeight * config.ChunkHeight;
            int totalWidth  = config.WorldWidth * config.ChunkWidth;

            for (int lx = 0; lx < config.ChunkWidth; lx++)
            {
                int worldX = chunkX * config.ChunkWidth + lx;

                // Surface height via Perlin
                int surfaceY = GetSurfaceY(worldX, totalHeight, seed);

                for (int ly = 0; ly < config.ChunkHeight; ly++)
                {
                    int worldY = chunkY * config.ChunkHeight + ly;

                    // ── Foreground ──────────────────────────────
                    int fgId = 0;

                    if (worldY <= BEDROCK_Y)
                    {
                        fgId = ID_BEDROCK;
                    }
                    else if (worldY < surfaceY - STONE_START)
                    {
                        // Deep underground: stone with cave carving
                        float caveNoise = Perlin3D(worldX * 0.08f, worldY * 0.08f, seed * 0.01f);
                        if (caveNoise < 0.35f)
                            fgId = ID_STONE;
                    }
                    else if (worldY < surfaceY - DIRT_DEPTH)
                    {
                        // Mid: dirt + stone mix
                        float n = Mathf.PerlinNoise(worldX * 0.1f + seed, worldY * 0.1f);
                        fgId = n > 0.5f ? ID_STONE : ID_CAVE_DIRT;
                    }
                    else if (worldY < surfaceY)
                    {
                        fgId = ID_DIRT;
                    }
                    else if (worldY == surfaceY)
                    {
                        fgId = GetSurfaceBlock(worldX, seed);
                    }
                    // Above surface = air (0)

                    // ── Background ──────────────────────────────
                    int bgId = worldY <= surfaceY
                        ? (worldY < surfaceY - STONE_START ? ID_BG_STONE : ID_BG_DIRT)
                        : ID_BG_SKY;

                    chunk.ForegroundTiles[lx, ly] = new TileData
                    {
                        ItemId = fgId,
                        Health = fgId != 0 ? ItemDatabase.Instance.GetItem(fgId)?.Durability ?? 100 : 0
                    };

                    chunk.BackgroundTiles[lx, ly] = new TileData
                    {
                        ItemId = bgId,
                        Health = bgId != 0 ? 200 : 0
                    };
                }

                // Trees on surface
                if (ShouldPlaceTree(worldX, surfaceY, seed, rng))
                    PlaceTree(chunk, lx, surfaceY - chunkY * config.ChunkHeight + 1, rng);
            }

            return chunk;
        }

        private static int GetSurfaceY(int worldX, int totalHeight, int seed)
        {
            float n = Mathf.PerlinNoise(worldX * 0.03f + seed * 0.001f, 0.5f);
            int groundLevel = (int)(totalHeight * 0.55f);
            int variation = 15;
            return groundLevel + Mathf.RoundToInt((n - 0.5f) * variation * 2);
        }

        private static int GetSurfaceBlock(int worldX, int seed)
        {
            float biomeNoise = Mathf.PerlinNoise(worldX * 0.005f + seed * 0.002f, 100f);
            if (biomeNoise > 0.65f) return ID_SAND;
            return ID_GRASS;
        }

        private static bool ShouldPlaceTree(int worldX, int surfaceY, int seed, System.Random rng)
        {
            float n = Mathf.PerlinNoise(worldX * 0.15f + seed * 0.003f, 999f);
            return n > 0.75f && rng.NextDouble() < 0.4f;
        }

        private static void PlaceTree(ChunkData chunk, int localX, int localY, System.Random rng)
        {
            int trunkHeight = 3 + rng.Next(3);
            int canopyRadius = 2 + rng.Next(2);

            // Trunk
            for (int i = 0; i < trunkHeight; i++)
            {
                int ty = localY + i;
                if (ty >= 0 && ty < 60 && localX >= 0 && localX < 100)
                {
                    chunk.ForegroundTiles[localX, ty] = new TileData { ItemId = ID_WOOD, Health = 150 };
                    chunk.BackgroundTiles[localX, ty]  = new TileData { ItemId = ID_BG_DIRT, Health = 200 };
                }
            }

            // Canopy
            int topY = localY + trunkHeight;
            for (int dx = -canopyRadius; dx <= canopyRadius; dx++)
            {
                for (int dy = 0; dy <= canopyRadius; dy++)
                {
                    int lx = localX + dx;
                    int ly = topY + dy;
                    if (lx < 0 || lx >= 100 || ly < 0 || ly >= 60) continue;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist <= canopyRadius + 0.5f)
                    {
                        if (chunk.ForegroundTiles[lx, ly].IsEmpty)
                            chunk.ForegroundTiles[lx, ly] = new TileData { ItemId = ID_LEAVES, Health = 50 };
                    }
                }
            }
        }

        private static float Perlin3D(float x, float y, float z)
        {
            // Approximate 3D Perlin via 3 2D samples
            float ab = Mathf.PerlinNoise(x, y);
            float bc = Mathf.PerlinNoise(y, z);
            float ac = Mathf.PerlinNoise(x, z);
            float ba = Mathf.PerlinNoise(y, x);
            float cb = Mathf.PerlinNoise(z, y);
            float ca = Mathf.PerlinNoise(z, x);
            return (ab + bc + ac + ba + cb + ca) / 6f;
        }

        private static int GetSeed(string worldId)
        {
            int hash = 17;
            foreach (char c in worldId)
                hash = hash * 31 + c;
            return Mathf.Abs(hash);
        }
    }
}
