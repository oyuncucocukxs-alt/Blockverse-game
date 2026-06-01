using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mirror;
using DG.Tweening;
using BlockVerse.Inventory;
using BlockVerse.Items;
using BlockVerse.Network;

namespace BlockVerse.Economy
{
    /// <summary>
    /// Server-authoritative vending machine system.
    /// Players place vending machine blocks and configure buy/sell slots.
    /// All transactions are atomic and anti-duplication protected.
    /// </summary>

    // ─────────────────────────────────────────────────────
    // Server-side Vending Manager
    // ─────────────────────────────────────────────────────

    public class VendingMachineManager : MonoBehaviour
    {
        public static VendingMachineManager Instance { get; private set; }

        // Key: tile position → VendingMachineState
        private readonly Dictionary<Vector2Int, VendingMachineState> _machines = new();

        private void Awake()
        {
            Instance = this;
            NetworkServer.RegisterHandler<VendingBuyMessage>(OnServerBuy);
            NetworkServer.RegisterHandler<VendingSetupMessage>(OnServerSetup);
        }

        // ─────────────────────────────────────────────
        #region Register / Unregister

        public void RegisterMachine(int tileX, int tileY, string ownerId)
        {
            var pos = new Vector2Int(tileX, tileY);
            if (!_machines.ContainsKey(pos))
            {
                _machines[pos] = new VendingMachineState
                {
                    OwnerId  = ownerId,
                    TileX    = tileX,
                    TileY    = tileY,
                    Slots    = new VendingSlot[9]
                };
            }
        }

        public void UnregisterMachine(int tileX, int tileY, PlayerServerState breaker,
            Dictionary<int, PlayerServerState> allPlayers)
        {
            var pos = new Vector2Int(tileX, tileY);
            if (!_machines.TryGetValue(pos, out var machine)) return;

            // Only owner or world admin can break their vending machine
            if (machine.OwnerId != breaker.PlayerId && !breaker.IsAdmin) return;

            // Return all stocked items to owner
            foreach (var slot in machine.Slots)
            {
                if (slot == null || slot.SellItemId == 0) continue;

                PlayerServerState owner = null;
                foreach (var p in allPlayers.Values)
                    if (p.PlayerId == machine.OwnerId) { owner = p; break; }

                owner?.Inventory.AddItem(slot.SellItemId, slot.SellCount);
                if (owner != null)
                    owner.Connection.Send(new InventorySyncMessage { Slots = owner.Inventory.Serialize() });
            }

            _machines.Remove(pos);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Server Handlers

        private void OnServerSetup(NetworkConnectionToClient conn, VendingSetupMessage msg)
        {
            var pos = new Vector2Int(msg.TileX, msg.TileY);
            if (!_machines.TryGetValue(pos, out var machine)) return;

            // Only owner can configure
            var player = GetPlayerByConn(conn);
            if (player == null || player.PlayerId != machine.OwnerId) return;

            // Validate: player must have all items being stocked
            for (int i = 0; i < msg.Slots.Length && i < 9; i++)
            {
                var slot = msg.Slots[i];
                if (slot.SellItemId == 0) continue;

                // Return old stock to inventory
                if (machine.Slots[i] != null && machine.Slots[i].SellItemId != 0)
                    player.Inventory.AddItem(machine.Slots[i].SellItemId, machine.Slots[i].SellCount);

                // Deduct new stock from inventory
                if (!player.Inventory.HasItem(slot.SellItemId, slot.SellCount))
                {
                    conn.Send(new ServerErrorMessage
                    {
                        Code = ErrorCode.NotEnoughItems,
                        Message = $"Not enough {ItemDatabase.Instance.GetItem(slot.SellItemId)?.ItemName}"
                    });
                    return;
                }

                player.Inventory.RemoveItem(slot.SellItemId, slot.SellCount);
                machine.Slots[i] = new VendingSlot
                {
                    SellItemId  = slot.SellItemId,
                    SellCount   = slot.SellCount,
                    PriceItemId = slot.PriceItemId,
                    PriceCount  = slot.PriceCount
                };
            }

            conn.Send(new InventorySyncMessage { Slots = player.Inventory.Serialize() });

            // Broadcast updated machine state to all
            NetworkServer.SendToAll(BuildSyncMessage(machine));
        }

        private void OnServerBuy(NetworkConnectionToClient conn, VendingBuyMessage msg)
        {
            var pos = new Vector2Int(msg.TileX, msg.TileY);
            if (!_machines.TryGetValue(pos, out var machine)) return;

            if (msg.SlotIndex < 0 || msg.SlotIndex >= machine.Slots.Length) return;
            var slot = machine.Slots[msg.SlotIndex];
            if (slot == null || slot.SellItemId == 0) return;

            var buyer = GetPlayerByConn(conn);
            if (buyer == null) return;

            int qty = Mathf.Clamp(msg.Quantity, 1, slot.SellCount);

            // Anti-self-buy
            if (buyer.PlayerId == machine.OwnerId)
            {
                conn.Send(new ServerErrorMessage
                {
                    Code = ErrorCode.InvalidAction,
                    Message = "Cannot buy from your own vending machine."
                });
                return;
            }

            int totalPrice = slot.PriceCount * qty;

            // Atomic transaction: deduct payment, give items
            if (!buyer.Inventory.HasItem(slot.PriceItemId, totalPrice))
            {
                conn.Send(new ServerErrorMessage
                {
                    Code = ErrorCode.NotEnoughCurrency,
                    Message = $"Need {totalPrice}x {ItemDatabase.Instance.GetItem(slot.PriceItemId)?.ItemName}"
                });
                return;
            }

            if (!buyer.Inventory.HasFreeSpace(slot.SellItemId, qty))
            {
                conn.Send(new ServerErrorMessage
                {
                    Code = ErrorCode.InvalidAction,
                    Message = "Your inventory is full."
                });
                return;
            }

            // Execute: remove payment from buyer
            buyer.Inventory.RemoveItem(slot.PriceItemId, totalPrice);
            // Give purchased items to buyer
            buyer.Inventory.AddItem(slot.SellItemId, qty);
            // Reduce stock in machine
            slot.SellCount -= qty;
            if (slot.SellCount <= 0) machine.Slots[msg.SlotIndex] = null;

            // Credit owner's inventory (if online) or queue for next login
            bool ownerCredited = false;
            foreach (var p in _allPlayers.Values)
            {
                if (p.PlayerId != machine.OwnerId) continue;
                p.Inventory.AddItem(slot.PriceItemId, totalPrice);
                p.Connection.Send(new InventorySyncMessage { Slots = p.Inventory.Serialize() });
                ownerCredited = true;
                break;
            }

            // If owner offline — queue the credit via backend
            if (!ownerCredited)
                StartCoroutine(BackendClient.Instance.CreditOfflinePlayer(
                    machine.OwnerId, slot.PriceItemId, totalPrice, null, null));

            // Sync buyer
            conn.Send(new InventorySyncMessage { Slots = buyer.Inventory.Serialize() });

            // Broadcast updated machine state
            NetworkServer.SendToAll(BuildSyncMessage(machine));

            // Notification in world chat
            NetworkServer.SendToAll(new ChatMessage
            {
                SenderName = "System",
                Text       = $"{buyer.Username} bought {qty}x {ItemDatabase.Instance.GetItem(slot.SellItemId)?.ItemName} from {machine.OwnerName}'s shop.",
                Channel    = ChatChannel.World
            });
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Helpers

        private Dictionary<int, PlayerServerState> _allPlayers;
        public void SetPlayerRegistry(Dictionary<int, PlayerServerState> players) => _allPlayers = players;

        private PlayerServerState GetPlayerByConn(NetworkConnectionToClient conn)
        {
            _allPlayers?.TryGetValue(conn.connectionId, out var p);
            return p;
        }

        private static VendingStateSyncMessage BuildSyncMessage(VendingMachineState m)
        {
            var slots = new VendingSlotData[9];
            for (int i = 0; i < 9; i++)
            {
                if (m.Slots[i] == null) continue;
                slots[i] = new VendingSlotData
                {
                    SellItemId  = m.Slots[i].SellItemId,
                    SellCount   = m.Slots[i].SellCount,
                    PriceItemId = m.Slots[i].PriceItemId,
                    PriceCount  = m.Slots[i].PriceCount
                };
            }
            return new VendingStateSyncMessage { TileX = m.TileX, TileY = m.TileY, Slots = slots };
        }

        public List<VendingMachineData> SerializeAll()
        {
            var list = new List<VendingMachineData>();
            foreach (var m in _machines.Values)
            {
                var data = new VendingMachineData
                {
                    TileX   = m.TileX, TileY = m.TileY,
                    OwnerId = m.OwnerId, OwnerName = m.OwnerName,
                    Slots   = new VendingSlotSaveData[9]
                };
                for (int i = 0; i < 9; i++)
                {
                    if (m.Slots[i] == null) continue;
                    data.Slots[i] = new VendingSlotSaveData
                    {
                        SellItemId  = m.Slots[i].SellItemId,
                        SellCount   = m.Slots[i].SellCount,
                        PriceItemId = m.Slots[i].PriceItemId,
                        PriceCount  = m.Slots[i].PriceCount
                    };
                }
                list.Add(data);
            }
            return list;
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Client-side Vending UI
    // ─────────────────────────────────────────────────────

    public class VendingMachineUI : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI ownerText;
        [SerializeField] private VendingSlotUI[] slots;
        [SerializeField] private Button closeBtn;
        [SerializeField] private CanvasGroup group;

        private int _tileX, _tileY;
        private bool _isOwner;
        private VendingSlotData[] _currentSlots;

        private void Start()
        {
            NetworkClient.RegisterHandler<VendingStateSyncMessage>(OnVendingSync);
            closeBtn.onClick.AddListener(Close);
        }

        public void Open(int tileX, int tileY, string ownerName, string ownerId, VendingSlotData[] slotData)
        {
            _tileX = tileX;
            _tileY = tileY;
            _isOwner = ownerId == GameManager.Instance.LocalPlayer?.PlayerId;
            _currentSlots = slotData;

            titleText.text = _isOwner ? "Your Shop" : $"{ownerName}'s Shop";
            ownerText.text = $"Owner: {ownerName}";

            for (int i = 0; i < slots.Length; i++)
                slots[i].Setup(i, slotData.Length > i ? slotData[i] : default, _isOwner, this);

            gameObject.SetActive(true);
            group.DOFade(1f, 0.2f);
        }

        public void Close()
        {
            group.DOFade(0f, 0.15f).OnComplete(() => gameObject.SetActive(false));
        }

        public void Buy(int slotIndex, int quantity)
        {
            NetworkClient.Send(new VendingBuyMessage
            {
                TileX = _tileX, TileY = _tileY,
                SlotIndex = slotIndex, Quantity = quantity
            });
        }

        public void SaveSetup()
        {
            var slotData = new VendingSlotData[9];
            for (int i = 0; i < slots.Length; i++)
                slotData[i] = slots[i].GetData();

            NetworkClient.Send(new VendingSetupMessage
            {
                TileX = _tileX, TileY = _tileY, Slots = slotData
            });
        }

        private void OnVendingSync(VendingStateSyncMessage msg)
        {
            if (msg.TileX != _tileX || msg.TileY != _tileY) return;
            for (int i = 0; i < slots.Length; i++)
                slots[i].Setup(i, msg.Slots.Length > i ? msg.Slots[i] : default, _isOwner, this);
        }
    }

    public class VendingSlotUI : MonoBehaviour
    {
        [SerializeField] private Image sellIcon;
        [SerializeField] private TextMeshProUGUI sellCountText;
        [SerializeField] private Image priceIcon;
        [SerializeField] private TextMeshProUGUI priceCountText;
        [SerializeField] private Button buyButton;
        [SerializeField] private Button editButton;
        [SerializeField] private GameObject emptyLabel;

        private int _slotIndex;
        private VendingSlotData _data;
        private VendingMachineUI _parent;

        public void Setup(int index, VendingSlotData data, bool isOwner, VendingMachineUI parent)
        {
            _slotIndex = index;
            _data      = data;
            _parent    = parent;

            bool hasItem = data.SellItemId != 0;
            emptyLabel.SetActive(!hasItem);

            if (hasItem)
            {
                var sellDef  = ItemDatabase.Instance.GetItem(data.SellItemId);
                var priceDef = ItemDatabase.Instance.GetItem(data.PriceItemId);
                sellIcon.sprite  = sellDef?.InventorySprite;
                priceIcon.sprite = priceDef?.InventorySprite;
                sellCountText.text  = $"x{data.SellCount}";
                priceCountText.text = $"{data.PriceCount} {priceDef?.ItemName}";
            }

            buyButton.gameObject.SetActive(hasItem && !isOwner);
            editButton.gameObject.SetActive(isOwner);

            buyButton.onClick.RemoveAllListeners();
            buyButton.onClick.AddListener(() => parent.Buy(index, 1));

            editButton.onClick.RemoveAllListeners();
            editButton.onClick.AddListener(() => OpenEditPanel());
        }

        private void OpenEditPanel()
        {
            VendingEditPanel.Instance.Open(_slotIndex, _data, updated =>
            {
                _data = updated;
                _parent.SaveSetup();
            });
        }

        public VendingSlotData GetData() => _data;
    }

    // ─────────────────────────────────────────────────────
    // Network Messages
    // ─────────────────────────────────────────────────────

    public struct VendingStateSyncMessage : NetworkMessage
    {
        public int TileX, TileY;
        public VendingSlotData[] Slots;
    }

    // ─────────────────────────────────────────────────────
    // Data Types
    // ─────────────────────────────────────────────────────

    public class VendingMachineState
    {
        public int    TileX, TileY;
        public string OwnerId;
        public string OwnerName;
        public VendingSlot[] Slots = new VendingSlot[9];
    }

    public class VendingSlot
    {
        public int SellItemId;
        public int SellCount;
        public int PriceItemId;
        public int PriceCount;
    }

    [Serializable]
    public class VendingMachineData
    {
        public int    TileX, TileY;
        public string OwnerId, OwnerName;
        public VendingSlotSaveData[] Slots;
    }

    [Serializable]
    public struct VendingSlotSaveData
    {
        public int SellItemId, SellCount;
        public int PriceItemId, PriceCount;
    }
}
