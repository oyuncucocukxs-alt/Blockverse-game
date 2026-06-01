# BlockVerse — Production MMO Architecture

## Technology Stack
- **Engine**: Unity 2023 LTS (Universal Render Pipeline)
- **Networking**: Mirror Networking (KCP Transport for UDP, WebSocket for web)
- **Backend**: Node.js 20 LTS + Express + Socket.IO
- **Database**: MongoDB Atlas (sharded cluster)
- **Auth**: Firebase Auth (Google, Apple, Guest)
- **Asset Management**: Unity Addressables + CDN (Cloudflare)
- **Cache**: Redis 7
- **DevOps**: Docker + Docker Compose + NGINX

## System Architecture

```
[Mobile/PC Client] ──── Mirror TCP/UDP ──── [Game Server Cluster]
                                                     │
                    ┌────────────────────────────────┤
                    │                                │
             [Auth Service]                  [World Service]
             [Firebase Auth]                 [MongoDB Worlds]
                    │                                │
             [REST API]                      [Redis Cache]
             [Node.js]                       [Chunk Store]
```

## Directory Layout

### Unity Client
```
Assets/
  Scripts/
    Core/           - GameManager, BootLoader, AppConfig
    Network/        - NetworkManager, MessageTypes, Sync components
    World/          - WorldEngine, ChunkSystem, TileManager, Lighting
    Player/         - PlayerController, CharacterSystem, AnimationSystem
    Inventory/      - InventoryManager, ItemDatabase, DragDrop
    Items/          - ItemDefinition, ItemFactory, SeedItem, ToolItem
    Farming/        - FarmingManager, GrowthSystem, HarvestSystem
    Economy/        - CurrencySystem, TradeSystem, VendingMachine
    Social/         - ChatSystem, FriendsSystem, GuildSystem
    Security/       - AntiCheat, PacketValidator, MovementValidator
    UI/             - All UI panels, HUD, menus
    Admin/          - AdminPanel, ModerationTools
    Utils/          - Extensions, Helpers, Constants
```

### Node.js Backend
```
Backend/
  src/
    controllers/    - Route handlers
    models/         - MongoDB schemas
    middleware/     - Auth, rate limiting, validation
    services/       - Business logic
    routes/         - API routes
    utils/          - Helpers
```

## Network Protocol
- **Movement**: Unreliable UDP (Mirror KCP)
- **Block Changes**: Reliable ordered TCP
- **Inventory**: Reliable TCP with server validation
- **Chat**: Reliable TCP
- **Heartbeat**: Every 5 seconds

## Security Model
- Server authoritative for ALL game state
- Client sends intent, server validates and executes
- Anti-cheat: position validation, speed checks, inventory checksums
- Rate limiting on all actions
- JWT tokens with 24h expiry + refresh tokens
