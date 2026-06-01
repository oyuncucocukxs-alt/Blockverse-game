using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using BlockVerse.Core;
using BlockVerse.Network;

namespace BlockVerse.World
{
    /// <summary>
    /// Client-side world engine managing tilemaps, chunk loading,
    /// lighting, and tile state synchronization.
    /// </summary>
    public class WorldEngine : MonoBehaviour
    {
        [Header("Tilemaps")]
        [SerializeField] private Tilemap foregroundTilemap;
        [SerializeField] private Tilemap backgroundTilemap;
        [SerializeField] private Tilemap collisionTilemap; // invisible, for physics
        [SerializeField] private Tilemap liquidTilemap;

        [Header("References")]
        [SerializeField] private AppConfig config;
        [SerializeField] private LightingSystem lightingSystem;
        [SerializeField] private Camera mainCamera;

        private WorldData _worldData;
        private readonly Dictionary<Vector2Int, LoadedChunk> _loadedChunks = new();
        private Vector2Int _lastPlayerChunk = new(-999, -999);

        // Tile damage overlays: position → damage progress (0-1)
        private readonly Dictionary<Vector2Int, TileDamageOverlay> _damageOverlays = new();

        // World items (dropped items in world)
        private readonly Dictionary<int, WorldItemObject> _worldItems = new();

        public WorldData CurrentWorld => _worldData;
        public event Action<WorldData> OnWorldLoaded;
        public event Action OnWorldUnloaded;

        // ─────────────────────────────────────────────
        #region Initialization

        private void Awake()
        {
            RegisterNetworkHandlers();
        }

        private void RegisterNetworkHandlers()
        {
            NetworkClient.RegisterHandler<WorldLoadMessage>(OnReceiveWorldLoad);
            NetworkClient.RegisterHandler<ChunkDataMessage>(OnReceiveChunkData);
            NetworkClient.RegisterHandler<TileSyncMessage>(OnReceiveTileSync);
            NetworkClient.RegisterHandler<TileDamageMessage>(OnReceiveTileDamage);
            NetworkClient.RegisterHandler<ItemDropMessage>(OnReceiveItemDrop);
            NetworkClient.RegisterHandler<ItemPickupMessage>(OnReceiveItemPickup);
            NetworkClient.RegisterHandler<FarmingGrowthUpdateMessage>(OnReceiveGrowthUpdate);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region World Load / Unload

        private void OnReceiveWorldLoad(WorldLoadMessage msg)
        {
            UnloadCurrentWorld();

            _worldData = new WorldData
            {
                WorldId = msg.WorldId,
                Name = msg.WorldName,
                Width = msg.Width,
                Height = msg.Height,
                OwnerId = msg.OwnerId
            };

            Debug.Log($"[WorldEngine] Entered world: {msg.WorldName}");
            OnWorldLoaded?.Invoke(_worldData);
        }

        public void UnloadCurrentWorld()
        {
            if (_worldData == null) return;

            foregroundTilemap.ClearAllTiles();
            backgroundTilemap.ClearAllTiles();
            collisionTilemap.ClearAllTiles();
            liquidTilemap.ClearAllTiles();

            _loadedChunks.Clear();
            _damageOverlays.Clear();

            foreach (var item in _worldItems.Values)
                if (item != null) Destroy(item.gameObject);
            _worldItems.Clear();

            lightingSystem.Clear();
            _worldData = null;
            OnWorldUnloaded?.Invoke();
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Chunk Loading

        private void OnReceiveChunkData(ChunkDataMessage msg)
        {
            var chunk = ChunkData.Deserialize(msg.Chunk);
            if (chunk == null) return;

            var chunkPos = new Vector2Int(chunk.ChunkX, chunk.ChunkY);
            _loadedChunks[chunkPos] = new LoadedChunk { Data = chunk };

            RenderChunk(chunk);
        }

        private void RenderChunk(ChunkData chunk)
        {
            int startX = chunk.ChunkX * config.ChunkWidth;
            int startY = chunk.ChunkY * config.ChunkHeight;

            for (int x = 0; x < config.ChunkWidth; x++)
            {
                for (int y = 0; y < config.ChunkHeight; y++)
                {
                    int worldX = startX + x;
                    int worldY = startY + y;
                    var pos = new Vector3Int(worldX, worldY, 0);

                    // Background
                    var bgTile = chunk.BackgroundTiles[x, y];
                    if (bgTile.ItemId != 0)
                    {
                        var tile = TileRegistry.GetRenderTile(bgTile.ItemId, TileLayer.Background);
                        backgroundTilemap.SetTile(pos, tile);
                    }

                    // Foreground
                    var fgTile = chunk.ForegroundTiles[x, y];
                    if (fgTile.ItemId != 0)
                    {
                        var tile = TileRegistry.GetRenderTile(fgTile.ItemId, TileLayer.Foreground);
                        foregroundTilemap.SetTile(pos, tile);

                        // Set collision
                        var itemDef = ItemDatabase.Instance.GetItem(fgTile.ItemId);
                        if (itemDef != null && itemDef.HasCollision)
                            collisionTilemap.SetTile(pos, TileRegistry.CollisionTile);
                    }
                }
            }

            // Update lighting for this chunk
            lightingSystem.RecalculateChunk(chunk);
        }

        private void Update()
        {
            if (_worldData == null) return;

            var player = PlayerController.LocalInstance;
            if (player == null) return;

            // Check if player moved to a new chunk
            int cx = Mathf.FloorToInt(player.transform.position.x / config.ChunkWidth);
            int cy = Mathf.FloorToInt(player.transform.position.y / config.ChunkHeight);
            var currentChunk = new Vector2Int(cx, cy);

            if (currentChunk != _lastPlayerChunk)
            {
                _lastPlayerChunk = currentChunk;
                UpdateVisibleChunks(currentChunk);
            }
        }

        private void UpdateVisibleChunks(Vector2Int center)
        {
            var required = new HashSet<Vector2Int>();

            for (int dx = -config.ViewDistance; dx <= config.ViewDistance; dx++)
            {
                for (int dy = -config.ViewDistance; dy <= config.ViewDistance; dy++)
                {
                    required.Add(center + new Vector2Int(dx, dy));
                }
            }

            // Request missing chunks
            foreach (var pos in required)
            {
                if (!_loadedChunks.ContainsKey(pos))
                {
                    NetworkClient.Send(new ChunkRequestMessage { ChunkX = pos.x, ChunkY = pos.y });
                }
            }

            // Unload distant chunks to save memory
            var toUnload = new List<Vector2Int>();
            foreach (var pos in _loadedChunks.Keys)
            {
                if (Mathf.Abs(pos.x - center.x) > config.ViewDistance + 1 ||
                    Mathf.Abs(pos.y - center.y) > config.ViewDistance + 1)
                {
                    toUnload.Add(pos);
                }
            }

            foreach (var pos in toUnload)
                UnloadChunk(pos);
        }

        private void UnloadChunk(Vector2Int chunkPos)
        {
            if (!_loadedChunks.TryGetValue(chunkPos, out var loaded)) return;

            int startX = chunkPos.x * config.ChunkWidth;
            int startY = chunkPos.y * config.ChunkHeight;

            for (int x = 0; x < config.ChunkWidth; x++)
            {
                for (int y = 0; y < config.ChunkHeight; y++)
                {
                    var pos = new Vector3Int(startX + x, startY + y, 0);
                    foregroundTilemap.SetTile(pos, null);
                    backgroundTilemap.SetTile(pos, null);
                    collisionTilemap.SetTile(pos, null);
                }
            }

            _loadedChunks.Remove(chunkPos);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Tile Sync (from server)

        private void OnReceiveTileSync(TileSyncMessage msg)
        {
            var pos = new Vector3Int(msg.X, msg.Y, 0);
            var tilemap = msg.IsBackground ? backgroundTilemap : foregroundTilemap;

            if (msg.ItemId == 0)
            {
                // Block removed
                tilemap.SetTile(pos, null);
                if (!msg.IsBackground)
                    collisionTilemap.SetTile(pos, null);

                RemoveDamageOverlay(new Vector2Int(msg.X, msg.Y));

                // Update chunk data
                UpdateLocalChunkTile(msg.X, msg.Y, msg.IsBackground, new TileData());
            }
            else
            {
                // Block placed
                var renderTile = TileRegistry.GetRenderTile(msg.ItemId, msg.IsBackground ? TileLayer.Background : TileLayer.Foreground);
                tilemap.SetTile(pos, renderTile);

                var itemDef = ItemDatabase.Instance.GetItem(msg.ItemId);
                if (!msg.IsBackground && itemDef != null && itemDef.HasCollision)
                    collisionTilemap.SetTile(pos, TileRegistry.CollisionTile);

                var newTile = new TileData { ItemId = msg.ItemId, Health = msg.Health };
                UpdateLocalChunkTile(msg.X, msg.Y, msg.IsBackground, newTile);

                // Play place sound
                AudioManager.Instance.PlaySfx("block_place", new Vector2(msg.X, msg.Y));
            }

            lightingSystem.RecalculateTile(msg.X, msg.Y);
        }

        private void OnReceiveTileDamage(TileDamageMessage msg)
        {
            var pos = new Vector2Int(msg.X, msg.Y);
            float progress = 1f - ((float)msg.Health / msg.MaxHealth);

            if (!_damageOverlays.TryGetValue(pos, out var overlay))
            {
                overlay = DamageOverlayPool.Get(new Vector3(msg.X + 0.5f, msg.Y + 0.5f, -0.1f));
                _damageOverlays[pos] = overlay;
            }

            overlay.SetProgress(progress);
            AudioManager.Instance.PlaySfx("block_hit", new Vector2(msg.X, msg.Y));
        }

        private void RemoveDamageOverlay(Vector2Int pos)
        {
            if (_damageOverlays.TryGetValue(pos, out var overlay))
            {
                DamageOverlayPool.Return(overlay);
                _damageOverlays.Remove(pos);
            }
        }

        private void UpdateLocalChunkTile(int worldX, int worldY, bool background, TileData tile)
        {
            int chunkX = worldX / config.ChunkWidth;
            int chunkY = worldY / config.ChunkHeight;
            var chunkPos = new Vector2Int(chunkX, chunkY);

            if (!_loadedChunks.TryGetValue(chunkPos, out var loaded)) return;

            int localX = worldX % config.ChunkWidth;
            int localY = worldY % config.ChunkHeight;

            if (background)
                loaded.Data.BackgroundTiles[localX, localY] = tile;
            else
                loaded.Data.ForegroundTiles[localX, localY] = tile;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Farming Growth

        private void OnReceiveGrowthUpdate(FarmingGrowthUpdateMessage msg)
        {
            var pos = new Vector3Int(msg.TileX, msg.TileY, 0);
            // Update tile sprite based on growth stage
            int chunkX = msg.TileX / config.ChunkWidth;
            int chunkY = msg.TileY / config.ChunkHeight;

            if (_loadedChunks.TryGetValue(new Vector2Int(chunkX, chunkY), out var chunk))
            {
                int localX = msg.TileX % config.ChunkWidth;
                int localY = msg.TileY % config.ChunkHeight;
                var tile = chunk.Data.ForegroundTiles[localX, localY];

                var renderTile = TileRegistry.GetFarmingTile(tile.ItemId, msg.GrowthStage);
                foregroundTilemap.SetTile(pos, renderTile);
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region World Items (dropped)

        private void OnReceiveItemDrop(ItemDropMessage msg)
        {
            var prefab = ItemDatabase.Instance.GetItem(msg.ItemId)?.WorldPrefab;
            if (prefab == null) return;

            var go = Instantiate(prefab, new Vector3(msg.Position.x, msg.Position.y, 0), Quaternion.identity);
            var worldItem = go.GetComponent<WorldItemObject>();
            worldItem.Initialize(msg.WorldItemId, msg.ItemId, msg.Count);
            _worldItems[msg.WorldItemId] = worldItem;
        }

        private void OnReceiveItemPickup(ItemPickupMessage msg)
        {
            if (_worldItems.TryGetValue(msg.WorldItemId, out var item))
            {
                if (msg.PickedUpBy == GameManager.Instance.LocalPlayer?.PlayerId)
                    AudioManager.Instance.PlaySfx("item_pickup");

                item.PlayPickupEffect(() =>
                {
                    Destroy(item.gameObject);
                    _worldItems.Remove(msg.WorldItemId);
                });
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Query API

        public TileData GetTileAt(int worldX, int worldY, bool background)
        {
            int chunkX = worldX / config.ChunkWidth;
            int chunkY = worldY / config.ChunkHeight;

            if (_loadedChunks.TryGetValue(new Vector2Int(chunkX, chunkY), out var chunk))
            {
                int lx = worldX % config.ChunkWidth;
                int ly = worldY % config.ChunkHeight;
                return background ? chunk.Data.BackgroundTiles[lx, ly] : chunk.Data.ForegroundTiles[lx, ly];
            }

            return TileData.Empty;
        }

        public bool IsTileLoaded(int worldX, int worldY)
        {
            int chunkX = worldX / config.ChunkWidth;
            int chunkY = worldY / config.ChunkHeight;
            return _loadedChunks.ContainsKey(new Vector2Int(chunkX, chunkY));
        }

        #endregion
    }

    // ─────────────────────────────────────────────
    // Supporting types
    // ─────────────────────────────────────────────

    public class LoadedChunk
    {
        public ChunkData Data;
    }

    public enum TileLayer { Foreground, Background }
}
