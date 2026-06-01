using UnityEngine;
using BlockVerse.Inventory;
using BlockVerse.Items;
using BlockVerse.Network;

namespace BlockVerse.Server
{
    /// <summary>
    /// Server-side processor for all inventory slot actions.
    /// Validates every operation before modifying authoritative state.
    /// </summary>
    public static class InventoryActionProcessor
    {
        private const int TRASH_ITEM_SLOT = -1; // sentinel

        public static void Process(PlayerServerState player, InventoryActionMessage msg)
        {
            switch (msg.ActionType)
            {
                case InventoryActionType.Move:
                    ProcessMove(player, msg);
                    break;
                case InventoryActionType.Split:
                    ProcessSplit(player, msg);
                    break;
                case InventoryActionType.Drop:
                    ProcessDrop(player, msg);
                    break;
                case InventoryActionType.Use:
                    ProcessUse(player, msg);
                    break;
                case InventoryActionType.Equip:
                    ProcessEquip(player, msg);
                    break;
                case InventoryActionType.Unequip:
                    ProcessUnequip(player, msg);
                    break;
                case InventoryActionType.Trash:
                    ProcessTrash(player, msg);
                    break;
            }
        }

        // ─────────────────────────────────────────────
        #region Move

        private static void ProcessMove(PlayerServerState player, InventoryActionMessage msg)
        {
            if (!ValidateSlot(msg.FromSlot) || !ValidateSlot(msg.ToSlot)) return;
            if (msg.FromSlot == msg.ToSlot) return;
            if (msg.Count <= 0) return;

            player.Inventory.MoveSlot(msg.FromSlot, msg.ToSlot, msg.Count);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Split

        private static void ProcessSplit(PlayerServerState player, InventoryActionMessage msg)
        {
            if (!ValidateSlot(msg.FromSlot) || !ValidateSlot(msg.ToSlot)) return;
            if (msg.FromSlot == msg.ToSlot) return;
            if (msg.Count <= 0) return;

            var src = player.Inventory.GetSlot(msg.FromSlot);
            if (src == null || src.IsEmpty) return;

            int splitCount = Mathf.Clamp(msg.Count, 1, src.Count - 1);
            if (splitCount <= 0) return;

            player.Inventory.MoveSlot(msg.FromSlot, msg.ToSlot, splitCount);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Drop

        private static void ProcessDrop(PlayerServerState player, InventoryActionMessage msg)
        {
            if (!ValidateSlot(msg.FromSlot)) return;

            var src = player.Inventory.GetSlot(msg.FromSlot);
            if (src == null || src.IsEmpty) return;

            int count = msg.Count > 0 ? Mathf.Min(msg.Count, src.Count) : src.Count;
            int itemId = src.ItemId;

            player.Inventory.RemoveItem(itemId, count);

            // Spawn world item at player position
            WorldItemSpawner.SpawnAt(
                player.Position,
                itemId,
                count,
                player.PlayerId
            );
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Use (Consumable)

        private static void ProcessUse(PlayerServerState player, InventoryActionMessage msg)
        {
            if (!ValidateSlot(msg.FromSlot)) return;

            var src = player.Inventory.GetSlot(msg.FromSlot);
            if (src == null || src.IsEmpty) return;

            var def = ItemDatabase.Instance.GetItem(src.ItemId);
            if (def == null || !def.IsConsumable) return;

            // Apply consumable effect
            if (def.HealAmount > 0)
                player.ApplyHeal(def.HealAmount);

            if (def.SpeedBoostDuration > 0)
                player.ApplySpeedBoost(def.SpeedBoostMultiplier, def.SpeedBoostDuration);

            player.Inventory.RemoveItem(src.ItemId, 1);

            // Notify player of effect
            player.Connection.Send(new ServerEffectMessage
            {
                EffectType  = EffectType.Consumable,
                ItemId      = src.ItemId,
                Value       = def.HealAmount,
                Duration    = def.SpeedBoostDuration
            });
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Equip

        private static void ProcessEquip(PlayerServerState player, InventoryActionMessage msg)
        {
            if (!ValidateSlot(msg.FromSlot)) return;

            var src = player.Inventory.GetSlot(msg.FromSlot);
            if (src == null || src.IsEmpty) return;

            var def = ItemDatabase.Instance.GetItem(src.ItemId);
            if (def == null || !def.IsWearable) return;

            // Unequip current item in that slot (if any)
            int currentItemId = player.Equipment.GetSlotItem(def.WearableSlot);
            if (currentItemId != 0)
            {
                bool added = player.Inventory.AddItem(currentItemId, 1);
                if (!added) return; // Inventory full — can't swap
            }

            // Equip new item
            player.Equipment.SetSlotItem(def.WearableSlot, src.ItemId);
            player.Inventory.RemoveItem(src.ItemId, 1);

            // Broadcast appearance update
            BroadcastAppearance(player);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Unequip

        private static void ProcessUnequip(PlayerServerState player, InventoryActionMessage msg)
        {
            if (msg.FromSlot < 0 || msg.FromSlot > 8) return; // equipment slot index

            var slot = (WearableSlot)msg.FromSlot;
            int itemId = player.Equipment.GetSlotItem(slot);
            if (itemId == 0) return;

            if (!player.Inventory.HasFreeSpace(itemId, 1)) return;

            player.Inventory.AddItem(itemId, 1);
            player.Equipment.SetSlotItem(slot, 0);

            BroadcastAppearance(player);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Trash

        private static void ProcessTrash(PlayerServerState player, InventoryActionMessage msg)
        {
            if (!ValidateSlot(msg.FromSlot)) return;

            var src = player.Inventory.GetSlot(msg.FromSlot);
            if (src == null || src.IsEmpty) return;

            var def = ItemDatabase.Instance.GetItem(src.ItemId);

            // Prevent trashing locked/bound items
            if (def != null && def.IsLock)
            {
                player.Connection.Send(new ServerErrorMessage
                {
                    Code = ErrorCode.InvalidAction,
                    Message = "Cannot trash locked items."
                });
                return;
            }

            int count = msg.Count > 0 ? Mathf.Min(msg.Count, src.Count) : src.Count;
            player.Inventory.RemoveItem(src.ItemId, count);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Helpers

        private static bool ValidateSlot(int slot) =>
            slot >= 0 && slot < 36;

        private static void BroadcastAppearance(PlayerServerState player)
        {
            var msg = new PlayerAppearanceMessage
            {
                PlayerId   = player.PlayerId,
                Appearance = player.GetCurrentAppearance()
            };
            Mirror.NetworkServer.SendToAll(msg);
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // World Item Spawner (server-side)
    // ─────────────────────────────────────────────────────

    public static class WorldItemSpawner
    {
        private static int _nextId = 1;

        public static void SpawnAt(Vector2 position, int itemId, int count, string droppedBy)
        {
            int worldItemId = _nextId++;

            // Random spread
            float angle  = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float spread = Random.Range(0.3f, 1.2f);
            var spawnPos = position + new Vector2(
                Mathf.Cos(angle) * spread,
                Mathf.Sin(angle) * spread + 0.5f
            );

            Mirror.NetworkServer.SendToAll(new ItemDropMessage
            {
                WorldItemId = worldItemId,
                ItemId      = itemId,
                Count       = count,
                Position    = spawnPos
            });

            // Register pickup detection (server watches for collision)
            WorldItemTracker.Register(worldItemId, itemId, count, spawnPos, droppedBy);
        }
    }

    // ─────────────────────────────────────────────────────
    // Network Messages
    // ─────────────────────────────────────────────────────

    public struct ServerEffectMessage : Mirror.NetworkMessage
    {
        public EffectType EffectType;
        public int        ItemId;
        public int        Value;
        public float      Duration;
    }

    public struct PlayerAppearanceMessage : Mirror.NetworkMessage
    {
        public string         PlayerId;
        public AppearanceData Appearance;
    }

    public enum EffectType : byte { Consumable, Buff, Debuff }

    // ─────────────────────────────────────────────────────
    // Equipment Slots (server-side)
    // ─────────────────────────────────────────────────────

    public class ServerEquipmentSlots
    {
        private readonly int[] _slots = new int[9]; // indexed by WearableSlot enum

        public int  GetSlotItem(WearableSlot slot) => _slots[(int)slot];
        public void SetSlotItem(WearableSlot slot, int itemId) => _slots[(int)slot] = itemId;

        public AppearanceData ToAppearance() => new()
        {
            HatItemId   = _slots[(int)WearableSlot.Head],
            ShirtItemId = _slots[(int)WearableSlot.Shirt],
            PantsItemId = _slots[(int)WearableSlot.Pants],
            ShoeItemId  = _slots[(int)WearableSlot.Shoes],
            HandItemId  = _slots[(int)WearableSlot.Hand],
            BackItemId  = _slots[(int)WearableSlot.Back],
        };
    }

    // ─────────────────────────────────────────────────────
    // World Item Tracker (server-side pickup detection)
    // ─────────────────────────────────────────────────────

    public static class WorldItemTracker
    {
        private class TrackedItem
        {
            public int     WorldItemId;
            public int     ItemId;
            public int     Count;
            public Vector2 Position;
            public string  DroppedBy;
            public float   SpawnTime;
            public bool    PickupProtected; // grace period: only dropper can pick up initially
        }

        private static readonly System.Collections.Generic.Dictionary<int, TrackedItem>
            _items = new();

        private const float PICKUP_GRACE   = 3f;  // seconds before others can pick up
        private const float DESPAWN_TIME   = 300f; // 5 minutes

        public static void Register(int worldItemId, int itemId, int count, Vector2 pos, string droppedBy)
        {
            _items[worldItemId] = new TrackedItem
            {
                WorldItemId     = worldItemId,
                ItemId          = itemId,
                Count           = count,
                Position        = pos,
                DroppedBy       = droppedBy,
                SpawnTime       = UnityEngine.Time.time,
                PickupProtected = true,
            };
        }

        public static bool TryPickup(int worldItemId, PlayerServerState player)
        {
            if (!_items.TryGetValue(worldItemId, out var item)) return false;

            float age = UnityEngine.Time.time - item.SpawnTime;

            // Grace period check
            if (item.PickupProtected && age < PICKUP_GRACE &&
                item.DroppedBy != player.PlayerId) return false;

            // Despawn check
            if (age > DESPAWN_TIME)
            {
                _items.Remove(worldItemId);
                Mirror.NetworkServer.SendToAll(new ItemPickupMessage
                    { WorldItemId = worldItemId, PickedUpBy = "despawn" });
                return false;
            }

            // Give item
            if (!player.Inventory.AddItem(item.ItemId, item.Count)) return false;

            _items.Remove(worldItemId);
            Mirror.NetworkServer.SendToAll(new ItemPickupMessage
                { WorldItemId = worldItemId, PickedUpBy = player.PlayerId });

            player.Connection.Send(new InventorySyncMessage
                { Slots = player.Inventory.Serialize() });

            return true;
        }

        public static void Tick()
        {
            float now = UnityEngine.Time.time;

            // Lift grace period
            foreach (var item in _items.Values)
                if (item.PickupProtected && now - item.SpawnTime > PICKUP_GRACE)
                    item.PickupProtected = false;

            // Despawn old items
            var toRemove = new System.Collections.Generic.List<int>();
            foreach (var kv in _items)
                if (now - kv.Value.SpawnTime > DESPAWN_TIME) toRemove.Add(kv.Key);

            foreach (var id in toRemove)
            {
                Mirror.NetworkServer.SendToAll(new ItemPickupMessage
                    { WorldItemId = id, PickedUpBy = "despawn" });
                _items.Remove(id);
            }
        }
    }
}
