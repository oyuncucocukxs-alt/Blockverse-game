using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using BlockVerse.Core;
using BlockVerse.World;
using BlockVerse.Player;
using BlockVerse.Security;

namespace BlockVerse.Network
{
    /// <summary>
    /// Central Mirror NetworkManager for BlockVerse.
    /// Handles connection lifecycle, player spawning, and server-side world authority.
    /// </summary>
    public class NetworkGameManager : NetworkManager
    {
        public static new NetworkGameManager Instance => singleton as NetworkGameManager;

        [Header("BlockVerse")]
        [SerializeField] private AppConfig config;
        [SerializeField] private GameObject playerPrefab;

        public bool IsConnected => NetworkClient.isConnected;
        public bool IsServer => NetworkServer.active;
        public bool IsClient => NetworkClient.active;

        // Server-side: maps connectionId → PlayerServerState
        private readonly Dictionary<int, PlayerServerState> _connectedPlayers = new();

        // Server-side world data
        private WorldData _currentWorldData;

        public event Action OnClientConnected;
        public event Action OnClientDisconnected;
        public event Action<PlayerServerState> OnPlayerJoined;
        public event Action<PlayerServerState> OnPlayerLeft;

        // ─────────────────────────────────────────────
        #region Client Connection

        public void StartClient(string address, int port)
        {
            networkAddress = address;
            GetComponent<TelepathyTransport>().port = (ushort)port;
            StartClient();
        }

        public void Disconnect()
        {
            if (IsClient) StopClient();
            if (IsServer) StopServer();
        }

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            Debug.Log("[Network] Connected to server.");

            // Send authentication handshake
            var authMsg = new AuthRequestMessage
            {
                Token = AuthService.Instance.CurrentUser.Token,
                WorldId = GameManager.Instance.LocalPlayer?.LastWorldId ?? "hub"
            };
            NetworkClient.Send(authMsg);

            OnClientConnected?.Invoke();
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            Debug.Log("[Network] Disconnected from server.");
            OnClientDisconnected?.Invoke();

            if (GameManager.Instance.CurrentState == GameState.InWorld)
                GameManager.Instance.SetState(GameState.MainMenu);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Server Lifecycle

        public override void OnStartServer()
        {
            base.OnStartServer();
            Debug.Log("[Server] Started.");

            // Register server-side message handlers
            NetworkServer.RegisterHandler<AuthRequestMessage>(OnServerReceiveAuth);
            NetworkServer.RegisterHandler<BlockBreakRequestMessage>(OnServerReceiveBlockBreak);
            NetworkServer.RegisterHandler<BlockPlaceRequestMessage>(OnServerReceiveBlockPlace);
            NetworkServer.RegisterHandler<PlayerMoveMessage>(OnServerReceivePlayerMove);
            NetworkServer.RegisterHandler<ChatMessage>(OnServerReceiveChat);
            NetworkServer.RegisterHandler<InventoryActionMessage>(OnServerReceiveInventoryAction);
            NetworkServer.RegisterHandler<TradeRequestMessage>(OnServerReceiveTradeRequest);

            // Load world data for this server instance
            StartCoroutine(LoadServerWorldData());
        }

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);
            Debug.Log($"[Server] Client connected: {conn.connectionId}");
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            if (_connectedPlayers.TryGetValue(conn.connectionId, out var playerState))
            {
                // Save player data before disconnect
                StartCoroutine(SavePlayerOnDisconnect(playerState));
                OnPlayerLeft?.Invoke(playerState);
                _connectedPlayers.Remove(conn.connectionId);

                // Notify other players
                var leaveMsg = new PlayerLeaveMessage { PlayerId = playerState.PlayerId };
                NetworkServer.SendToAll(leaveMsg);
            }

            base.OnServerDisconnect(conn);
        }

        private IEnumerator LoadServerWorldData()
        {
            string worldId = WorldServerConfig.CurrentWorldId;
            bool loaded = false;

            yield return WorldPersistenceService.Instance.LoadWorld(
                worldId,
                data =>
                {
                    _currentWorldData = data;
                    loaded = true;
                },
                err =>
                {
                    Debug.LogError($"[Server] Failed to load world {worldId}: {err}");
                    loaded = true;
                }
            );

            if (_currentWorldData == null)
            {
                Debug.Log("[Server] Generating new world...");
                _currentWorldData = WorldGenerator.GenerateWorld(worldId, config);
                yield return WorldPersistenceService.Instance.SaveWorld(_currentWorldData, null, null);
            }

            Debug.Log($"[Server] World '{worldId}' ready.");
        }

        private IEnumerator SavePlayerOnDisconnect(PlayerServerState player)
        {
            yield return BackendClient.Instance.SavePlayerData(player.ToPlayerData(), null, null);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Server Message Handlers

        private void OnServerReceiveAuth(NetworkConnectionToClient conn, AuthRequestMessage msg)
        {
            // Validate JWT token with backend
            StartCoroutine(ValidateAndJoinPlayer(conn, msg));
        }

        private IEnumerator ValidateAndJoinPlayer(NetworkConnectionToClient conn, AuthRequestMessage msg)
        {
            TokenValidationResult result = null;
            yield return BackendClient.Instance.ValidateToken(
                msg.Token,
                r => result = r,
                err => Debug.LogError($"[Server] Auth error: {err}")
            );

            if (result == null || !result.Valid)
            {
                conn.Disconnect();
                yield break;
            }

            // Check if player is banned from this world
            if (_currentWorldData.BannedPlayers.Contains(result.PlayerId))
            {
                conn.Send(new ServerErrorMessage { Code = ErrorCode.Banned, Message = "You are banned from this world." });
                conn.Disconnect();
                yield break;
            }

            // Check player limit
            if (_connectedPlayers.Count >= config.MaxPlayersPerWorld)
            {
                conn.Send(new ServerErrorMessage { Code = ErrorCode.WorldFull, Message = "World is full." });
                conn.Disconnect();
                yield break;
            }

            // Load player data
            PlayerData playerData = null;
            yield return BackendClient.Instance.GetPlayerData(
                result.PlayerId,
                d => playerData = d,
                err => Debug.LogError($"[Server] GetPlayerData error: {err}")
            );

            if (playerData == null)
            {
                conn.Disconnect();
                yield break;
            }

            // Create server-side player state
            var playerState = new PlayerServerState(conn, playerData, config);
            _connectedPlayers[conn.connectionId] = playerState;

            // Spawn player object
            var spawnPos = GetSpawnPosition(playerData);
            var playerObj = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            NetworkServer.AddPlayerForConnection(conn, playerObj);

            var networkPlayer = playerObj.GetComponent<NetworkPlayer>();
            networkPlayer.ServerInitialize(playerState);

            // Send world data to new player
            conn.Send(new WorldLoadMessage
            {
                WorldId = _currentWorldData.WorldId,
                WorldName = _currentWorldData.Name,
                Width = _currentWorldData.Width,
                Height = _currentWorldData.Height,
                OwnerId = _currentWorldData.OwnerId
            });

            // Send initial chunks around spawn
            SendInitialChunks(conn, spawnPos);

            // Send existing players list
            foreach (var existing in _connectedPlayers.Values)
            {
                if (existing.ConnectionId == conn.connectionId) continue;
                conn.Send(new PlayerJoinMessage
                {
                    PlayerId = existing.PlayerId,
                    Username = existing.Username,
                    Position = existing.Position,
                    Appearance = existing.Appearance
                });
            }

            // Notify all players of new joiner
            var joinMsg = new PlayerJoinMessage
            {
                PlayerId = playerState.PlayerId,
                Username = playerState.Username,
                Position = spawnPos,
                Appearance = playerState.Appearance
            };
            NetworkServer.SendToAll(joinMsg);

            OnPlayerJoined?.Invoke(playerState);
            Debug.Log($"[Server] Player '{playerState.Username}' joined world '{_currentWorldData.WorldId}'.");
        }

        private void OnServerReceiveBlockBreak(NetworkConnectionToClient conn, BlockBreakRequestMessage msg)
        {
            if (!_connectedPlayers.TryGetValue(conn.connectionId, out var player)) return;

            // Anti-cheat: validate reach
            float dist = Vector2.Distance(player.Position, new Vector2(msg.X, msg.Y));
            if (dist > config.MaxBlockReach + 1f)
            {
                AntiCheatLogger.Log(player.PlayerId, AntiCheatViolation.BlockReachHack, $"dist={dist}");
                return;
            }

            // Rate limit
            if (!player.BlockBreakRateLimit.Allow())
            {
                AntiCheatLogger.Log(player.PlayerId, AntiCheatViolation.ActionSpam, "block_break");
                return;
            }

            // Validate block exists
            var chunk = _currentWorldData.GetChunkAt(msg.X, msg.Y);
            if (chunk == null) return;

            int localX = msg.X % config.ChunkWidth;
            int localY = msg.Y % config.ChunkHeight;
            var tile = msg.IsBackground ? chunk.BackgroundTiles[localX, localY] : chunk.ForegroundTiles[localX, localY];

            if (tile.ItemId == 0) return; // Nothing to break

            var itemDef = ItemDatabase.Instance.GetItem(tile.ItemId);
            if (itemDef == null) return;

            // Check permissions
            if (!CanPlayerBuildAt(player, msg.X, msg.Y))
            {
                conn.Send(new ServerErrorMessage { Code = ErrorCode.NoPermission, Message = "You don't have build permission here." });
                return;
            }

            // Apply damage to tile
            tile.Health -= player.GetBreakPower(itemDef);

            if (tile.Health <= 0)
            {
                // Tile destroyed
                if (msg.IsBackground)
                    chunk.BackgroundTiles[localX, localY] = TileData.Empty;
                else
                    chunk.ForegroundTiles[localX, localY] = TileData.Empty;

                chunk.IsDirty = true;

                // Drop item to player inventory
                AddItemToPlayer(player, itemDef.BreakDrop, itemDef.BreakDropCount);

                // Broadcast tile removal to all
                var syncMsg = new TileSyncMessage
                {
                    X = msg.X, Y = msg.Y,
                    IsBackground = msg.IsBackground,
                    ItemId = 0, Health = 0
                };
                NetworkServer.SendToAll(syncMsg);
            }
            else
            {
                // Broadcast damage state
                var damageMsg = new TileDamageMessage
                {
                    X = msg.X, Y = msg.Y,
                    IsBackground = msg.IsBackground,
                    Health = tile.Health,
                    MaxHealth = itemDef.Durability
                };
                NetworkServer.SendToAll(damageMsg);
            }

            // Auto-save dirty chunks periodically
            ChunkSaveQueue.Enqueue(chunk);
        }

        private void OnServerReceiveBlockPlace(NetworkConnectionToClient conn, BlockPlaceRequestMessage msg)
        {
            if (!_connectedPlayers.TryGetValue(conn.connectionId, out var player)) return;

            float dist = Vector2.Distance(player.Position, new Vector2(msg.X, msg.Y));
            if (dist > config.MaxBlockReach + 1f)
            {
                AntiCheatLogger.Log(player.PlayerId, AntiCheatViolation.BlockReachHack, $"dist={dist}");
                return;
            }

            if (!player.BlockPlaceRateLimit.Allow()) return;

            // Verify player has the item
            if (!player.Inventory.HasItem(msg.ItemId, 1))
            {
                conn.Send(new ServerErrorMessage { Code = ErrorCode.NotEnoughItems, Message = "You don't have that item." });
                return;
            }

            // Validate target tile is empty
            var chunk = _currentWorldData.GetChunkAt(msg.X, msg.Y);
            if (chunk == null) return;

            int localX = msg.X % config.ChunkWidth;
            int localY = msg.Y % config.ChunkHeight;
            var existing = msg.IsBackground ? chunk.BackgroundTiles[localX, localY] : chunk.ForegroundTiles[localX, localY];

            if (existing.ItemId != 0) return; // Tile occupied

            if (!CanPlayerBuildAt(player, msg.X, msg.Y))
            {
                conn.Send(new ServerErrorMessage { Code = ErrorCode.NoPermission, Message = "No build permission." });
                return;
            }

            // Place tile
            var itemDef = ItemDatabase.Instance.GetItem(msg.ItemId);
            if (itemDef == null) return;

            var newTile = new TileData
            {
                ItemId = msg.ItemId,
                Health = itemDef.Durability,
                PlacedBy = player.PlayerId,
                PlacedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            if (msg.IsBackground)
                chunk.BackgroundTiles[localX, localY] = newTile;
            else
                chunk.ForegroundTiles[localX, localY] = newTile;

            chunk.IsDirty = true;

            // Consume item from inventory
            player.Inventory.RemoveItem(msg.ItemId, 1);
            conn.Send(new InventorySyncMessage { Slots = player.Inventory.Serialize() });

            // Broadcast to all
            var syncMsg = new TileSyncMessage
            {
                X = msg.X, Y = msg.Y,
                IsBackground = msg.IsBackground,
                ItemId = msg.ItemId,
                Health = itemDef.Durability
            };
            NetworkServer.SendToAll(syncMsg);

            ChunkSaveQueue.Enqueue(chunk);
        }

        private void OnServerReceivePlayerMove(NetworkConnectionToClient conn, PlayerMoveMessage msg)
        {
            if (!_connectedPlayers.TryGetValue(conn.connectionId, out var player)) return;

            // Anti-cheat: speed validation
            float expectedMaxDist = config.MaxAllowedSpeed * config.NetworkTickRate * 2f; // 2x buffer
            float moveDelta = Vector2.Distance(player.Position, msg.Position);

            if (moveDelta > config.TeleportDetectionDist)
            {
                AntiCheatLogger.Log(player.PlayerId, AntiCheatViolation.Teleport, $"delta={moveDelta}");
                // Snap back
                conn.Send(new PositionCorrectionMessage { Position = player.Position });
                return;
            }

            if (moveDelta > expectedMaxDist)
            {
                AntiCheatLogger.Log(player.PlayerId, AntiCheatViolation.SpeedHack, $"delta={moveDelta}");
                conn.Send(new PositionCorrectionMessage { Position = player.Position });
                return;
            }

            player.Position = msg.Position;
            player.FlipX = msg.FlipX;
            player.AnimState = msg.AnimState;

            // Relay movement to all other clients
            var relay = new PlayerMoveRelayMessage
            {
                PlayerId = player.PlayerId,
                Position = msg.Position,
                FlipX = msg.FlipX,
                AnimState = msg.AnimState
            };

            foreach (var other in _connectedPlayers.Values)
            {
                if (other.ConnectionId != conn.connectionId)
                    other.Connection.Send(relay);
            }
        }

        private void OnServerReceiveChat(NetworkConnectionToClient conn, ChatMessage msg)
        {
            if (!_connectedPlayers.TryGetValue(conn.connectionId, out var player)) return;

            // Rate limit
            if (!player.ChatRateLimit.Allow()) return;

            // Sanitize
            string sanitized = ChatSanitizer.Sanitize(msg.Text, 200);
            if (string.IsNullOrWhiteSpace(sanitized)) return;

            // Check if muted
            if (player.IsMuted) return;

            var outMsg = new ChatMessage
            {
                SenderId = player.PlayerId,
                SenderName = player.Username,
                Text = sanitized,
                Channel = msg.Channel,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            // Route by channel
            if (msg.Channel == ChatChannel.World)
                NetworkServer.SendToAll(outMsg);
            else if (msg.Channel == ChatChannel.Global)
                NetworkServer.SendToAll(outMsg); // In production, relay to global chat microservice

            // Persist to chat log
            ChatPersistenceService.Instance.LogMessage(outMsg, _currentWorldData.WorldId);
        }

        private void OnServerReceiveInventoryAction(NetworkConnectionToClient conn, InventoryActionMessage msg)
        {
            if (!_connectedPlayers.TryGetValue(conn.connectionId, out var player)) return;

            InventoryActionProcessor.Process(player, msg);
            conn.Send(new InventorySyncMessage { Slots = player.Inventory.Serialize() });
        }

        private void OnServerReceiveTradeRequest(NetworkConnectionToClient conn, TradeRequestMessage msg)
        {
            if (!_connectedPlayers.TryGetValue(conn.connectionId, out var player)) return;
            TradeSystem.Instance.HandleTradeRequest(player, msg, _connectedPlayers);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Helpers

        private Vector3 GetSpawnPosition(PlayerData data)
        {
            if (data.LastPosition != Vector2.zero)
                return new Vector3(data.LastPosition.x, data.LastPosition.y, 0);

            return new Vector3(_currentWorldData.SpawnX, _currentWorldData.SpawnY, 0);
        }

        private void SendInitialChunks(NetworkConnectionToClient conn, Vector3 position)
        {
            int chunkX = Mathf.FloorToInt(position.x / config.ChunkWidth);
            int chunkY = Mathf.FloorToInt(position.y / config.ChunkHeight);

            for (int dx = -config.ViewDistance; dx <= config.ViewDistance; dx++)
            {
                for (int dy = -config.ViewDistance; dy <= config.ViewDistance; dy++)
                {
                    var chunk = _currentWorldData.GetChunk(chunkX + dx, chunkY + dy);
                    if (chunk != null)
                        conn.Send(new ChunkDataMessage { Chunk = chunk.Serialize() });
                }
            }
        }

        private bool CanPlayerBuildAt(PlayerServerState player, int x, int y)
        {
            // World owner always can build
            if (_currentWorldData.OwnerId == player.PlayerId) return true;
            // Admins can build
            if (player.IsAdmin) return true;
            // Check build permission lock at this tile
            return _currentWorldData.HasBuildPermission(player.PlayerId, x, y);
        }

        private void AddItemToPlayer(PlayerServerState player, int itemId, int count)
        {
            if (itemId == 0 || count == 0) return;
            player.Inventory.AddItem(itemId, count);
            player.Connection.Send(new InventorySyncMessage { Slots = player.Inventory.Serialize() });
        }

        private static readonly Queue<ChunkData> ChunkSaveQueue = new();

        private void Update()
        {
            if (!IsServer) return;

            // Process chunk saves in batches
            int saves = 0;
            while (ChunkSaveQueue.Count > 0 && saves < 5)
            {
                var chunk = ChunkSaveQueue.Dequeue();
                if (chunk.IsDirty)
                {
                    WorldPersistenceService.Instance.SaveChunkAsync(chunk);
                    chunk.IsDirty = false;
                }
                saves++;
            }
        }

        #endregion
    }
}
