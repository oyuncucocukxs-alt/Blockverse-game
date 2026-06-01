# BlockVerse — Production MMO Game Framework

A complete, production-ready 2D online sandbox MMO inspired by Growtopia.
Built with Unity, Mirror Networking, Node.js, MongoDB, Redis, and Firebase.

---

## 🗂️ Project Structure

```
BlockVerse/
├── Unity/Assets/Scripts/
│   ├── Core/           GameManager, AppConfig, BackendClient, AuthService, AudioManager
│   ├── Network/        NetworkGameManager, MessageTypes (all Mirror messages)
│   ├── World/          WorldEngine, ChunkData, WorldGenerator, LightingSystem,
│   │                   TileRegistry, WorldPersistenceService
│   ├── Player/         PlayerController, NetworkPlayer, PlayerRegistry
│   ├── Inventory/      InventoryManager, ServerInventory
│   ├── Items/          ItemDefinition, ItemDatabase, CraftingSystem
│   ├── Farming/        FarmingManager
│   ├── Economy/        TradeSystem, VendingMachine, CurrencySystem,
│   │                   ShopSystem, IAPManager, BattlePass
│   ├── Social/         GuildSystem, EmoteSystem
│   ├── Security/       AntiCheat, PacketValidator, ChatSanitizer
│   ├── Server/         InventoryActionProcessor, WorldItemTracker
│   ├── UI/             UIManager, LoginUI, InventoryUI, ChatUI, WorldSearchUI,
│   │                   PlayerProfileUI, SettingsUI, MinimapUI, AdminPanelUI,
│   │                   MobileControls
│   └── Utils/          PoolManager, ObjectPool
│
├── Backend/
│   ├── src/
│   │   ├── server.js                 Entry point
│   │   ├── models/index.js           All MongoDB schemas
│   │   ├── middleware/
│   │   │   ├── auth.js               JWT + Firebase validation
│   │   │   └── crashReporter.js      Error logging
│   │   ├── routes/
│   │   │   ├── auth.js               Login, register, refresh
│   │   │   ├── players.js            Profile, friends, session save
│   │   │   ├── worlds.js             CRUD, chunks, ban, permissions
│   │   │   ├── economy.js            Marketplace, shop, IAP, trades
│   │   │   ├── matchmaking.js        Server registry, heartbeat
│   │   │   ├── admin.js              Ban, mute, analytics, broadcast
│   │   │   └── guild_leaderboard_iap.js  Guild, leaderboard, IAP
│   │   └── sockets/
│   │       ├── chatSocket.js         Global chat + whispers
│   │       └── presenceSocket.js     Online status + friend presence
│   ├── package.json
│   ├── Dockerfile
│   └── .env.example
│
└── DevOps/
    ├── docker-compose.yml            Full stack orchestration
    ├── prometheus.yml                Monitoring
    ├── nginx/nginx.conf              Reverse proxy + SSL
    ├── docker/mongo-init.js          DB indexes + seed data
    └── scripts/deploy.sh             Deployment automation
```

---

## 🚀 Quick Start

### 1. Backend Setup

```bash
cd BlockVerse/Backend
cp .env.example .env
# Edit .env with your credentials

cd ../DevOps
./scripts/deploy.sh up
```

### 2. Unity Setup

1. Open Unity Hub → Add → `BlockVerse/Unity/`
2. Unity 2023 LTS (URP template)
3. Install packages:
   - **Mirror Networking** (via Package Manager or Asset Store)
   - **DoTween Pro** (Asset Store)
   - **Newtonsoft JSON** (`com.unity.nuget.newtonsoft-json`)
   - **Addressables** (`com.unity.addressables`)
   - **Unity IAP** (`com.unity.purchasing`)
   - **TextMeshPro** (built-in)
   - **SocketIOClient** (NuGet for Unity)

4. Configure `AppConfig` ScriptableObject:
   - Set `BackendApiUrl` → `https://api.blockverse.io/v1`
   - Set Firebase credentials

5. Build Addressables catalog
6. Build for target platform

### 3. Dedicated Server

```bash
# Linux headless build from Unity
# Build → Linux → Server Build → check "Headless Mode"

# Run with:
./BlockVerse.x86_64 -worldId HUB -secret YOUR_SERVER_SECRET -serverId server-01
```

---

## 🏗️ Architecture

```
[Mobile/PC Client]
        │
        │ Mirror (KCP/TCP)
        ▼
[Unity Dedicated Server] ──── REST ────► [Node.js API]
        │                                      │
        │                               ┌──────┴──────┐
        │                           [MongoDB]      [Redis]
        │                               │
        └─── heartbeat ──────► [Matchmaker] ◄── [Client requests]
```

**Security Model:**
- Server authoritative for ALL game state
- Client sends *intent*, server validates and executes
- JWT access tokens (24h) + refresh tokens (30d)
- Game servers authenticate with shared secret header
- Anti-cheat: speed check, teleport detection, rate limiting, inventory checksums

---

## 🔧 Environment Variables

See `Backend/.env.example` for all required vars.

Critical values:
| Variable | Description |
|----------|-------------|
| `MONGO_URI` | MongoDB connection string |
| `REDIS_HOST` / `REDIS_PASSWORD` | Redis connection |
| `JWT_SECRET` | 64+ char random string |
| `GAME_SERVER_SECRET` | Shared secret for Unity ↔ API auth |
| `FIREBASE_ADMIN_SDK_JSON` | Full Firebase service account JSON |

---

## 📊 Systems Overview

| System | File | Notes |
|--------|------|-------|
| Multiplayer | `NetworkGameManager.cs` | Mirror, server-authoritative |
| World Engine | `WorldEngine.cs` | Chunk streaming, tilemap |
| World Gen | `WorldGenerator.cs` | Perlin noise, biomes, trees |
| Lighting | `LightingSystem.cs` | BFS flood-fill, day/night |
| Inventory | `InventoryManager.cs` | Drag-drop, 36 slots, equipment |
| Crafting | `CraftingSystem.cs` | Recipe UI + server validation |
| Farming | `FarmingManager.cs` | Growth timers, rare drops |
| Trading | `TradeSystem.cs` | Atomic swap, anti-dupe |
| Vending | `VendingMachine.cs` | Player-owned shops |
| Economy | `ShopSystem.cs` | Crystal shop + Unity IAP |
| Battle Pass | `BattlePass.cs` | Seasonal XP track, rewards |
| Guilds | `GuildSystem.cs` | Roles, chat, invite system |
| Emotes | `EmoteSystem.cs` | 8-slot wheel, network sync |
| Anti-Cheat | `AntiCheat.cs` | Speed, teleport, rate limits |
| Chat | `ChatUI.cs` + `chatSocket.js` | World/Global/Whisper/Guild |
| Minimap | `MinimapUI.cs` | RenderTexture, player dots |
| Admin | `AdminPanelUI.cs` | Ban/mute/kick/analytics |

---

## 📦 Key Dependencies

### Unity
- Mirror Networking — multiplayer transport
- DoTween Pro — UI animations
- Addressables — asset streaming
- Unity IAP — in-app purchases
- SocketIOClient — Socket.IO connection

### Node.js
- express, socket.io, mongoose, ioredis
- firebase-admin — token verification
- jsonwebtoken — session management
- winston — structured logging

---

## 🔐 Security Notes

1. **Never** commit `.env` to source control
2. Rotate `JWT_SECRET` and `GAME_SERVER_SECRET` regularly
3. Enable MongoDB authentication in production
4. Use Redis `requirepass` in production
5. All chunk saves are server-authoritative (clients cannot inject tiles)
6. IAP receipts should be validated with Apple/Google APIs in production
   (see `iapRouter` in `guild_leaderboard_iap.js`)

---

## 📈 Scaling

- **Game servers**: Scale horizontally via `./deploy.sh scale-game N`
- **API**: Stateless Node.js — scale behind NGINX load balancer
- **MongoDB**: Upgrade to Atlas sharded cluster for > 1M players
- **Redis**: Redis Cluster for > 50K concurrent connections
- **Socket.IO**: Uses Redis adapter — horizontal scaling is automatic

---

*BlockVerse MMO Framework — 62 files, ~12,000 lines of production code*
