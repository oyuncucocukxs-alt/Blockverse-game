using Mirror;
using UnityEngine;
using BlockVerse.Inventory;

namespace BlockVerse.Network
{
    // ─────────────────────────────────────────────
    // AUTH
    // ─────────────────────────────────────────────

    public struct AuthRequestMessage : NetworkMessage
    {
        public string Token;
        public string WorldId;
    }

    public struct TokenValidationResult
    {
        public bool Valid;
        public string PlayerId;
        public string Username;
        public bool IsAdmin;
    }

    // ─────────────────────────────────────────────
    // CONNECTION / WORLD
    // ─────────────────────────────────────────────

    public struct WorldLoadMessage : NetworkMessage
    {
        public string WorldId;
        public string WorldName;
        public int Width;
        public int Height;
        public string OwnerId;
    }

    public struct ChunkDataMessage : NetworkMessage
    {
        public byte[] Chunk; // Serialized ChunkData
    }

    public struct ChunkRequestMessage : NetworkMessage
    {
        public int ChunkX;
        public int ChunkY;
    }

    // ─────────────────────────────────────────────
    // BLOCK SYSTEM
    // ─────────────────────────────────────────────

    public struct BlockBreakRequestMessage : NetworkMessage
    {
        public int X;
        public int Y;
        public bool IsBackground;
    }

    public struct BlockPlaceRequestMessage : NetworkMessage
    {
        public int X;
        public int Y;
        public bool IsBackground;
        public int ItemId;
    }

    public struct TileSyncMessage : NetworkMessage
    {
        public int X;
        public int Y;
        public bool IsBackground;
        public int ItemId;
        public int Health;
    }

    public struct TileDamageMessage : NetworkMessage
    {
        public int X;
        public int Y;
        public bool IsBackground;
        public int Health;
        public int MaxHealth;
    }

    // ─────────────────────────────────────────────
    // PLAYER MOVEMENT
    // ─────────────────────────────────────────────

    public struct PlayerMoveMessage : NetworkMessage
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public bool FlipX;
        public byte AnimState; // 0=idle,1=walk,2=jump,3=fall
        public long ClientTime; // ms timestamp for lag compensation
    }

    public struct PlayerMoveRelayMessage : NetworkMessage
    {
        public string PlayerId;
        public Vector2 Position;
        public bool FlipX;
        public byte AnimState;
    }

    public struct PositionCorrectionMessage : NetworkMessage
    {
        public Vector2 Position;
    }

    public struct PlayerJoinMessage : NetworkMessage
    {
        public string PlayerId;
        public string Username;
        public Vector2 Position;
        public AppearanceData Appearance;
    }

    public struct PlayerLeaveMessage : NetworkMessage
    {
        public string PlayerId;
    }

    // ─────────────────────────────────────────────
    // INVENTORY
    // ─────────────────────────────────────────────

    public struct InventoryActionMessage : NetworkMessage
    {
        public InventoryActionType ActionType;
        public int FromSlot;
        public int ToSlot;
        public int Count;
        public int ItemId;
    }

    public struct InventorySyncMessage : NetworkMessage
    {
        public InventorySlotData[] Slots;
    }

    public struct ItemDropMessage : NetworkMessage
    {
        public int WorldItemId;
        public int ItemId;
        public int Count;
        public Vector2 Position;
    }

    public struct ItemPickupMessage : NetworkMessage
    {
        public int WorldItemId;
        public string PickedUpBy;
    }

    // ─────────────────────────────────────────────
    // TRADING
    // ─────────────────────────────────────────────

    public struct TradeRequestMessage : NetworkMessage
    {
        public string TargetPlayerId;
    }

    public struct TradeResponseMessage : NetworkMessage
    {
        public string TradeId;
        public bool Accepted;
    }

    public struct TradeOfferMessage : NetworkMessage
    {
        public string TradeId;
        public InventorySlotData[] OfferedItems;
        public int OfferedCurrency;
    }

    public struct TradeConfirmMessage : NetworkMessage
    {
        public string TradeId;
        public bool Confirmed;
    }

    public struct TradeCancelMessage : NetworkMessage
    {
        public string TradeId;
    }

    public struct TradeCompleteMessage : NetworkMessage
    {
        public string TradeId;
        public bool Success;
        public InventorySlotData[] ReceivedItems;
    }

    // ─────────────────────────────────────────────
    // CHAT
    // ─────────────────────────────────────────────

    public struct ChatMessage : NetworkMessage
    {
        public string SenderId;
        public string SenderName;
        public string Text;
        public ChatChannel Channel;
        public long Timestamp;
        public string TargetPlayerId; // For whispers
    }

    public enum ChatChannel : byte
    {
        World = 0,
        Global = 1,
        Whisper = 2,
        Guild = 3,
        System = 4
    }

    // ─────────────────────────────────────────────
    // FARMING
    // ─────────────────────────────────────────────

    public struct FarmingGrowthUpdateMessage : NetworkMessage
    {
        public int TileX;
        public int TileY;
        public byte GrowthStage; // 0-7
        public bool IsReadyToHarvest;
    }

    public struct HarvestRequestMessage : NetworkMessage
    {
        public int TileX;
        public int TileY;
    }

    // ─────────────────────────────────────────────
    // VENDING / ECONOMY
    // ─────────────────────────────────────────────

    public struct VendingBuyMessage : NetworkMessage
    {
        public int TileX;
        public int TileY;
        public int SlotIndex;
        public int Quantity;
    }

    public struct VendingSetupMessage : NetworkMessage
    {
        public int TileX;
        public int TileY;
        public VendingSlotData[] Slots;
    }

    // ─────────────────────────────────────────────
    // WORLD MANAGEMENT
    // ─────────────────────────────────────────────

    public struct WorldBanPlayerMessage : NetworkMessage
    {
        public string TargetPlayerId;
        public string Reason;
    }

    public struct WorldKickPlayerMessage : NetworkMessage
    {
        public string TargetPlayerId;
        public string Reason;
    }

    public struct WorldPermissionMessage : NetworkMessage
    {
        public string TargetPlayerId;
        public BuildPermission Permission;
    }

    public enum BuildPermission : byte
    {
        None = 0,
        Build = 1,
        Admin = 2
    }

    // ─────────────────────────────────────────────
    // ERRORS
    // ─────────────────────────────────────────────

    public struct ServerErrorMessage : NetworkMessage
    {
        public ErrorCode Code;
        public string Message;
    }

    public enum ErrorCode : byte
    {
        Unknown = 0,
        Banned = 1,
        WorldFull = 2,
        NoPermission = 3,
        NotEnoughItems = 4,
        NotEnoughCurrency = 5,
        InvalidAction = 6,
        RateLimited = 7
    }

    // ─────────────────────────────────────────────
    // DATA STRUCTS (serializable via Mirror)
    // ─────────────────────────────────────────────

    [System.Serializable]
    public struct AppearanceData : NetworkMessage
    {
        public int HatItemId;
        public int ShirtItemId;
        public int PantsItemId;
        public int ShoeItemId;
        public int HandItemId;
        public int BackItemId;
        public int SkinColor;  // palette index
        public int EyeColor;
        public int HairColor;
        public int HairStyle;
    }

    [System.Serializable]
    public struct VendingSlotData
    {
        public int SellItemId;
        public int SellCount;
        public int PriceItemId;
        public int PriceCount;
    }

    public enum InventoryActionType : byte
    {
        Move = 0,
        Split = 1,
        Drop = 2,
        Use = 3,
        Equip = 4,
        Unequip = 5,
        Trash = 6
    }
}
