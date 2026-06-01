using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using BlockVerse.Core;

namespace BlockVerse.World
{
    /// <summary>
    /// Flood-fill based 2D tile lighting system.
    /// Background tiles block light; foreground solid tiles cast shadows.
    /// Point lights from torches/lamps are blended additively.
    /// </summary>
    public class LightingSystem : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Tilemap lightingTilemap;   // overlay tilemap (black→alpha)
        [SerializeField] private TileBase lightTile;         // single black tile we tint
        [SerializeField] private AppConfig config;

        [Header("Lighting Settings")]
        [SerializeField] private Color ambientDayColor   = new(1f,   0.95f, 0.85f, 0f);
        [SerializeField] private Color ambientNightColor = new(0.1f, 0.1f,  0.3f, 0.85f);
        [SerializeField] private float dayNightCycleSec  = 1200f;  // 20 min full cycle
        [SerializeField] private int   lightFalloff      = 2;       // per-tile reduction (0–15)
        [SerializeField] private int   maxLightLevel     = 15;

        // Light map: world position → light level (0-15)
        private readonly Dictionary<Vector2Int, byte> _lightMap    = new();
        private readonly Dictionary<Vector2Int, byte> _pointLights = new(); // torch/lamp positions

        private float _cycleTimer;
        private bool  _isDirty;

        // Chunk-dirty tracking: only re-render affected chunks
        private readonly HashSet<Vector2Int> _dirtyChunks = new();

        private Coroutine _rebuildCoroutine;

        // ─────────────────────────────────────────────
        #region Unity Lifecycle

        private void Update()
        {
            // Day/night cycle
            _cycleTimer += Time.deltaTime;
            if (_cycleTimer > dayNightCycleSec) _cycleTimer = 0f;

            float t = Mathf.PingPong(_cycleTimer / dayNightCycleSec * 2f, 1f);
            lightingTilemap.color = Color.Lerp(ambientNightColor, ambientDayColor, t);

            if (_isDirty && _rebuildCoroutine == null)
                _rebuildCoroutine = StartCoroutine(RebuildDirtyChunks());
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Public API

        public void Clear()
        {
            _lightMap.Clear();
            _pointLights.Clear();
            _dirtyChunks.Clear();
            lightingTilemap.ClearAllTiles();
        }

        /// <summary>Called when a chunk is loaded/rendered.</summary>
        public void RecalculateChunk(ChunkData chunk)
        {
            _dirtyChunks.Add(new Vector2Int(chunk.ChunkX, chunk.ChunkY));
            _isDirty = true;
        }

        /// <summary>Called when a single tile changes (place/break).</summary>
        public void RecalculateTile(int worldX, int worldY)
        {
            // Mark surrounding chunks dirty (light bleeds across chunk borders)
            int cx = worldX / config.ChunkWidth;
            int cy = worldY / config.ChunkHeight;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                _dirtyChunks.Add(new Vector2Int(cx + dx, cy + dy));

            _isDirty = true;
        }

        /// <summary>Register a point light source (torch, lamp, etc.).</summary>
        public void AddPointLight(int worldX, int worldY, byte intensity)
        {
            _pointLights[new Vector2Int(worldX, worldY)] = intensity;
            RecalculateTile(worldX, worldY);
        }

        public void RemovePointLight(int worldX, int worldY)
        {
            _pointLights.Remove(new Vector2Int(worldX, worldY));
            RecalculateTile(worldX, worldY);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Rebuild Coroutine

        private IEnumerator RebuildDirtyChunks()
        {
            yield return null; // wait one frame to batch changes

            var chunks = new HashSet<Vector2Int>(_dirtyChunks);
            _dirtyChunks.Clear();
            _isDirty = false;

            int processed = 0;
            foreach (var chunkPos in chunks)
            {
                FloodFillChunk(chunkPos);
                RenderChunkLighting(chunkPos);

                processed++;
                if (processed % 2 == 0) yield return null; // spread over frames
            }

            _rebuildCoroutine = null;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Flood Fill

        private void FloodFillChunk(Vector2Int chunkPos)
        {
            int startX = chunkPos.x * config.ChunkWidth;
            int startY = chunkPos.y * config.ChunkHeight;
            int endX   = startX + config.ChunkWidth;
            int endY   = startY + config.ChunkHeight;

            // Sky light: top row always = max
            for (int x = startX; x < endX; x++)
                _lightMap[new Vector2Int(x, endY - 1)] = (byte)maxLightLevel;

            // BFS flood fill downward
            var queue = new Queue<(Vector2Int pos, byte level)>();

            // Seed from sky + point lights in this chunk region
            for (int x = startX; x < endX; x++)
                queue.Enqueue((new Vector2Int(x, endY - 1), (byte)maxLightLevel));

            foreach (var kv in _pointLights)
                if (kv.Key.x >= startX && kv.Key.x < endX &&
                    kv.Key.y >= startY && kv.Key.y < endY)
                    queue.Enqueue((kv.Key, kv.Value));

            var visited = new HashSet<Vector2Int>();

            while (queue.Count > 0)
            {
                var (pos, level) = queue.Dequeue();
                if (visited.Contains(pos)) continue;
                visited.Add(pos);

                byte existing = _lightMap.GetValueOrDefault(pos, 0);
                if (level <= existing) continue;

                _lightMap[pos] = level;

                if (level <= lightFalloff) continue;
                byte next = (byte)(level - lightFalloff);

                // Propagate in 4 directions
                TryPropagate(pos + Vector2Int.up,    next, queue, visited);
                TryPropagate(pos + Vector2Int.down,  next, queue, visited);
                TryPropagate(pos + Vector2Int.left,  next, queue, visited);
                TryPropagate(pos + Vector2Int.right, next, queue, visited);
            }
        }

        private void TryPropagate(Vector2Int pos, byte level,
            Queue<(Vector2Int, byte)> queue, HashSet<Vector2Int> visited)
        {
            if (visited.Contains(pos)) return;

            // Check if foreground tile blocks light
            var worldEngine = WorldEngine.Instance;
            if (worldEngine != null)
            {
                var tile = worldEngine.GetTileAt(pos.x, pos.y, false);
                if (tile.ItemId != 0)
                {
                    var def = ItemDatabase.Instance.GetItem(tile.ItemId);
                    if (def != null && !def.IsTransparent)
                    {
                        level = (byte)Mathf.Max(0, level - 4); // Extra falloff for solid blocks
                    }
                }
            }

            if (level > 0) queue.Enqueue((pos, level));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Render Lighting to Tilemap

        private void RenderChunkLighting(Vector2Int chunkPos)
        {
            int startX = chunkPos.x * config.ChunkWidth;
            int startY = chunkPos.y * config.ChunkHeight;

            for (int x = startX; x < startX + config.ChunkWidth; x++)
            {
                for (int y = startY; y < startY + config.ChunkHeight; y++)
                {
                    var tilePos = new Vector3Int(x, y, 0);
                    byte lightLevel = _lightMap.GetValueOrDefault(new Vector2Int(x, y), 0);

                    if (lightLevel >= maxLightLevel)
                    {
                        // Fully lit — remove shadow tile
                        lightingTilemap.SetTile(tilePos, null);
                    }
                    else
                    {
                        // Place shadow tile with alpha based on darkness
                        float darkness = 1f - (float)lightLevel / maxLightLevel;
                        lightingTilemap.SetTile(tilePos, lightTile);
                        lightingTilemap.SetTileFlags(tilePos, TileFlags.None);
                        lightingTilemap.SetColor(tilePos, new Color(0, 0, 0, darkness));
                    }
                }
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Helpers

        public byte GetLightLevel(int worldX, int worldY)
            => _lightMap.GetValueOrDefault(new Vector2Int(worldX, worldY), 0);

        public float GetLightFraction(int worldX, int worldY)
            => GetLightLevel(worldX, worldY) / (float)maxLightLevel;

        #endregion
    }
}
