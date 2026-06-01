using System;
using System.Collections.Generic;
using UnityEngine;
using BlockVerse.Core;
using BlockVerse.Network;

namespace BlockVerse.Inventory
{
    /// <summary>
    /// Client-side inventory manager. Mirrors server authoritative state.
    /// All mutations are requested via network; UI reflects confirmed server state.
    /// </summary>
    public class InventoryManager : MonoBehaviour
    {
        public static InventoryManager Instance { get; private set; }

        [SerializeField] private AppConfig config;

        private InventorySlot[] _slots;       // Main inventory (0..35)
        private InventorySlot[] _hotbarSlots; // Mirror of slots 0..8
        private EquipmentSlots _equipment;

        public event Action<int, InventorySlot> OnSlotChanged;
        public event Action<EquipmentSlots> OnEquipmentChanged;

        private void Awake()
        {
            Instance = this;
            _slots = new InventorySlot[config.MaxInventorySlots];
            _hotbarSlots = new InventorySlot[config.HotbarSlots];
            _equipment = new EquipmentSlots();

            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = new InventorySlot();

            NetworkClient.RegisterHandler<InventorySyncMessage>(OnServerInventorySync);
        }

        // ─────────────────────────────────────────────
        #region Server Sync

        private void OnServerInventorySync(InventorySyncMessage msg)
        {
            for (int i = 0; i < msg.Slots.Length && i < _slots.Length; i++)
            {
                _slots[i].ItemId = msg.Slots[i].ItemId;
                _slots[i].Count = msg.Slots[i].Count;
                _slots[i].Metadata = msg.Slots[i].Metadata;
                OnSlotChanged?.Invoke(i, _slots[i]);
            }

            // Update hotbar mirror
            for (int i = 0; i < config.HotbarSlots; i++)
                _hotbarSlots[i] = _slots[i];
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Query

        public InventorySlot GetSlot(int index)
        {
            if (index < 0 || index >= _slots.Length) return null;
            return _slots[index];
        }

        public InventorySlot GetHotbarItem(int slot)
        {
            if (slot < 0 || slot >= _hotbarSlots.Length) return null;
            return _hotbarSlots[slot];
        }

        public int GetItemCount(int itemId)
        {
            int total = 0;
            foreach (var s in _slots)
                if (s.ItemId == itemId) total += s.Count;
            return total;
        }

        public bool HasItem(int itemId, int count = 1) => GetItemCount(itemId) >= count;

        public EquipmentSlots Equipment => _equipment;

        #endregion

        // ─────────────────────────────────────────────
        #region Client Requests (send to server)

        public void MoveItem(int fromSlot, int toSlot)
        {
            NetworkClient.Send(new InventoryActionMessage
            {
                ActionType = InventoryActionType.Move,
                FromSlot = fromSlot,
                ToSlot = toSlot,
                Count = _slots[fromSlot].Count
            });
        }

        public void SplitItem(int fromSlot, int toSlot, int count)
        {
            if (_slots[fromSlot].Count < count) return;

            NetworkClient.Send(new InventoryActionMessage
            {
                ActionType = InventoryActionType.Split,
                FromSlot = fromSlot,
                ToSlot = toSlot,
                Count = count
            });
        }

        public void DropItem(int slot, int count = -1)
        {
            int dropCount = count < 0 ? _slots[slot].Count : count;
            NetworkClient.Send(new InventoryActionMessage
            {
                ActionType = InventoryActionType.Drop,
                FromSlot = slot,
                Count = dropCount
            });
        }

        public void UseItem(int slot)
        {
            NetworkClient.Send(new InventoryActionMessage
            {
                ActionType = InventoryActionType.Use,
                FromSlot = slot,
                Count = 1
            });
        }

        public void EquipItem(int slot)
        {
            NetworkClient.Send(new InventoryActionMessage
            {
                ActionType = InventoryActionType.Equip,
                FromSlot = slot
            });
        }

        public void TrashItem(int slot)
        {
            NetworkClient.Send(new InventoryActionMessage
            {
                ActionType = InventoryActionType.Trash,
                FromSlot = slot,
                Count = _slots[slot].Count
            });
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Serialization (for server state)

        public InventorySlotData[] Serialize()
        {
            var data = new InventorySlotData[_slots.Length];
            for (int i = 0; i < _slots.Length; i++)
                data[i] = _slots[i].ToData();
            return data;
        }

        #endregion
    }

    // ─────────────────────────────────────────────
    // Server-side inventory (authoritative)
    // ─────────────────────────────────────────────

    public class ServerInventory
    {
        private readonly InventorySlot[] _slots;
        private readonly int _size;
        private readonly int _maxStackSize;

        public ServerInventory(int size, int maxStackSize = 200)
        {
            _size = size;
            _maxStackSize = maxStackSize;
            _slots = new InventorySlot[size];
            for (int i = 0; i < size; i++)
                _slots[i] = new InventorySlot();
        }

        public bool HasItem(int itemId, int count)
        {
            int found = 0;
            foreach (var s in _slots)
                if (s.ItemId == itemId) found += s.Count;
            return found >= count;
        }

        public bool AddItem(int itemId, int count)
        {
            if (count <= 0) return true;
            var itemDef = ItemDatabase.Instance.GetItem(itemId);
            int maxStack = itemDef?.MaxStack ?? _maxStackSize;

            // Fill existing stacks first
            foreach (var slot in _slots)
            {
                if (slot.ItemId == itemId && slot.Count < maxStack)
                {
                    int canAdd = Mathf.Min(count, maxStack - slot.Count);
                    slot.Count += canAdd;
                    count -= canAdd;
                    if (count <= 0) return true;
                }
            }

            // Fill empty slots
            foreach (var slot in _slots)
            {
                if (slot.ItemId == 0)
                {
                    int canAdd = Mathf.Min(count, maxStack);
                    slot.ItemId = itemId;
                    slot.Count = canAdd;
                    count -= canAdd;
                    if (count <= 0) return true;
                }
            }

            return count <= 0; // Returns false if inventory full
        }

        public bool RemoveItem(int itemId, int count)
        {
            if (!HasItem(itemId, count)) return false;

            int remaining = count;
            foreach (var slot in _slots)
            {
                if (slot.ItemId == itemId)
                {
                    int remove = Mathf.Min(remaining, slot.Count);
                    slot.Count -= remove;
                    remaining -= remove;
                    if (slot.Count <= 0)
                        slot.Clear();
                    if (remaining <= 0) return true;
                }
            }
            return true;
        }

        public bool MoveSlot(int from, int to, int count)
        {
            if (from < 0 || from >= _size || to < 0 || to >= _size) return false;
            var src = _slots[from];
            var dst = _slots[to];

            if (src.ItemId == 0) return false;
            count = Mathf.Min(count, src.Count);

            if (dst.ItemId == 0)
            {
                dst.ItemId = src.ItemId;
                dst.Count = count;
                dst.Metadata = src.Metadata;
                src.Count -= count;
                if (src.Count <= 0) src.Clear();
                return true;
            }

            if (dst.ItemId == src.ItemId)
            {
                var def = ItemDatabase.Instance.GetItem(src.ItemId);
                int maxStack = def?.MaxStack ?? 200;
                int canMove = Mathf.Min(count, maxStack - dst.Count);
                dst.Count += canMove;
                src.Count -= canMove;
                if (src.Count <= 0) src.Clear();
                return true;
            }

            // Swap
            if (count == src.Count)
            {
                var tmp = dst.Clone();
                dst.CopyFrom(src);
                src.CopyFrom(tmp);
                return true;
            }

            return false;
        }

        public InventorySlotData[] Serialize()
        {
            var data = new InventorySlotData[_size];
            for (int i = 0; i < _size; i++)
                data[i] = _slots[i].ToData();
            return data;
        }

        public static ServerInventory Deserialize(InventorySlotData[] data)
        {
            var inv = new ServerInventory(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                inv._slots[i].ItemId = data[i].ItemId;
                inv._slots[i].Count = data[i].Count;
                inv._slots[i].Metadata = data[i].Metadata;
            }
            return inv;
        }
    }

    // ─────────────────────────────────────────────
    // Data types
    // ─────────────────────────────────────────────

    public class InventorySlot
    {
        public int ItemId;
        public int Count;
        public string Metadata; // JSON string for special item data

        public bool IsEmpty => ItemId == 0 || Count <= 0;

        public void Clear()
        {
            ItemId = 0;
            Count = 0;
            Metadata = null;
        }

        public InventorySlot Clone() => new() { ItemId = ItemId, Count = Count, Metadata = Metadata };
        public void CopyFrom(InventorySlot other) { ItemId = other.ItemId; Count = other.Count; Metadata = other.Metadata; }

        public InventorySlotData ToData() => new() { ItemId = ItemId, Count = Count, Metadata = Metadata };
    }

    [Serializable]
    public struct InventorySlotData
    {
        public int ItemId;
        public int Count;
        public string Metadata;
    }

    [Serializable]
    public class EquipmentSlots
    {
        public int HatItemId;
        public int ShirtItemId;
        public int PantsItemId;
        public int ShoeItemId;
        public int HandItemId;
        public int BackItemId;
        public int NeckItemId;
        public int FaceItemId;
    }
}
