using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using DG.Tweening;
using BlockVerse.Inventory;
using BlockVerse.Items;

namespace BlockVerse.UI
{
    /// <summary>
    /// Full drag-and-drop inventory UI.
    /// Communicates slot moves to InventoryManager which sends to server.
    /// </summary>
    public class InventoryUI : MonoBehaviour
    {
        [Header("Grid")]
        [SerializeField] private Transform inventoryGrid;
        [SerializeField] private Transform hotbarGrid;
        [SerializeField] private InventorySlotUI slotPrefab;

        [Header("Equipment")]
        [SerializeField] private EquipmentSlotUI hatSlot;
        [SerializeField] private EquipmentSlotUI shirtSlot;
        [SerializeField] private EquipmentSlotUI pantsSlot;
        [SerializeField] private EquipmentSlotUI shoesSlot;
        [SerializeField] private EquipmentSlotUI handSlot;
        [SerializeField] private EquipmentSlotUI backSlot;

        [Header("Tooltip")]
        [SerializeField] private ItemTooltip tooltip;

        [Header("Currency")]
        [SerializeField] private TextMeshProUGUI gemsText;

        private InventorySlotUI[] _slots;
        private InventorySlotUI _dragSource;
        private DragIcon _dragIcon;

        private void Start()
        {
            BuildGrid();
            InventoryManager.Instance.OnSlotChanged += OnSlotChanged;
            InventoryManager.Instance.OnEquipmentChanged += OnEquipmentChanged;
            RefreshAll();
        }

        private void BuildGrid()
        {
            _slots = new InventorySlotUI[36];

            for (int i = 0; i < 36; i++)
            {
                int slotIndex = i;
                var slot = Instantiate(slotPrefab, i < 9 ? hotbarGrid : inventoryGrid);
                slot.Initialize(slotIndex, this);
                _slots[i] = slot;
            }
        }

        private void RefreshAll()
        {
            for (int i = 0; i < 36; i++)
                _slots[i].Refresh(InventoryManager.Instance.GetSlot(i));

            var player = GameManager.Instance.LocalPlayer;
            if (player != null) gemsText.text = player.Gems.ToString("N0");
        }

        private void OnSlotChanged(int index, InventorySlot slot)
        {
            if (index < _slots.Length)
                _slots[index].Refresh(slot);

            if (index == -1) // Currency update
                gemsText.text = GameManager.Instance.LocalPlayer?.Gems.ToString("N0");
        }

        private void OnEquipmentChanged(EquipmentSlots eq)
        {
            hatSlot.Refresh(eq.HatItemId);
            shirtSlot.Refresh(eq.ShirtItemId);
            pantsSlot.Refresh(eq.PantsItemId);
            shoesSlot.Refresh(eq.ShoeItemId);
            handSlot.Refresh(eq.HandItemId);
            backSlot.Refresh(eq.BackItemId);
        }

        // ─────────────────────────────────────────────
        #region Drag-and-Drop

        public void OnBeginDrag(InventorySlotUI source, PointerEventData eventData)
        {
            var slot = InventoryManager.Instance.GetSlot(source.SlotIndex);
            if (slot == null || slot.IsEmpty) return;

            _dragSource = source;
            _dragIcon = DragIconPool.Get();
            _dragIcon.SetItem(slot.ItemId, slot.Count);
            _dragIcon.transform.position = eventData.position;

            // Dim source slot
            source.SetDragging(true);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_dragIcon) _dragIcon.transform.position = eventData.position;
        }

        public void OnEndDrag(InventorySlotUI target, PointerEventData eventData)
        {
            if (_dragSource == null || _dragIcon == null) return;

            _dragSource.SetDragging(false);
            DragIconPool.Return(_dragIcon);
            _dragIcon = null;

            if (target == null || target == _dragSource)
            {
                _dragSource = null;
                return;
            }

            // Handle right-click split during drag (half quantity)
            bool split = Input.GetMouseButton(1);

            if (split)
            {
                var srcSlot = InventoryManager.Instance.GetSlot(_dragSource.SlotIndex);
                int halfCount = Mathf.Max(1, srcSlot.Count / 2);
                InventoryManager.Instance.SplitItem(_dragSource.SlotIndex, target.SlotIndex, halfCount);
            }
            else
            {
                InventoryManager.Instance.MoveItem(_dragSource.SlotIndex, target.SlotIndex);
            }

            _dragSource = null;
        }

        public void OnDropped(InventorySlotUI target, PointerEventData eventData)
        {
            // Handled in OnEndDrag
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Context Menu (right-click)

        public void OnSlotRightClick(InventorySlotUI slotUI)
        {
            var slot = InventoryManager.Instance.GetSlot(slotUI.SlotIndex);
            if (slot == null || slot.IsEmpty) return;

            var itemDef = ItemDatabase.Instance.GetItem(slot.ItemId);
            if (itemDef == null) return;

            var options = new List<ContextMenuOption>();

            if (itemDef.IsWearable)
                options.Add(new ContextMenuOption("Equip", () => InventoryManager.Instance.EquipItem(slotUI.SlotIndex)));
            if (itemDef.IsConsumable)
                options.Add(new ContextMenuOption("Use", () => InventoryManager.Instance.UseItem(slotUI.SlotIndex)));

            options.Add(new ContextMenuOption("Drop", () => InventoryManager.Instance.DropItem(slotUI.SlotIndex)));
            options.Add(new ContextMenuOption("Trash", () => ShowTrashConfirm(slotUI.SlotIndex)));

            ContextMenu.Show(Input.mousePosition, options);
        }

        private void ShowTrashConfirm(int slot)
        {
            ConfirmDialog.Show(
                "Trash Item",
                "This will permanently delete the item. Are you sure?",
                "Trash",
                () => InventoryManager.Instance.TrashItem(slot)
            );
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Tooltip

        public void ShowTooltip(int itemId, Vector3 position)
        {
            var def = ItemDatabase.Instance.GetItem(itemId);
            if (def == null) { tooltip.Hide(); return; }
            tooltip.Show(def, position);
        }

        public void HideTooltip() => tooltip.Hide();

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // InventorySlotUI
    // ─────────────────────────────────────────────────────

    public class InventorySlotUI : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler,
        IPointerClickHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        public int SlotIndex { get; private set; }
        private InventoryUI _parent;

        [SerializeField] private Image itemIcon;
        [SerializeField] private TextMeshProUGUI countText;
        [SerializeField] private Image selectionBorder;
        [SerializeField] private Image background;
        [SerializeField] private Image rarityBorder;

        private InventorySlot _currentSlot;
        private bool _isDragging;

        private static readonly Color[] RarityColors =
        {
            Color.white,                          // Common
            new Color(0.3f, 1f, 0.3f),            // Uncommon
            new Color(0.3f, 0.5f, 1f),            // Rare
            new Color(0.7f, 0.3f, 1f),            // Epic
            new Color(1f, 0.8f, 0.1f),            // Legendary
            new Color(1f, 0.4f, 0.1f),            // Exclusive
        };

        public void Initialize(int index, InventoryUI parent)
        {
            SlotIndex = index;
            _parent = parent;
        }

        public void Refresh(InventorySlot slot)
        {
            _currentSlot = slot;

            if (slot == null || slot.IsEmpty)
            {
                itemIcon.enabled = false;
                countText.text = "";
                rarityBorder.enabled = false;
                return;
            }

            var def = ItemDatabase.Instance.GetItem(slot.ItemId);
            if (def == null) return;

            itemIcon.enabled = true;
            itemIcon.sprite = def.InventorySprite;
            countText.text = slot.Count > 1 ? slot.Count.ToString() : "";

            rarityBorder.enabled = true;
            rarityBorder.color = RarityColors[(int)def.Rarity];

            if (!_isDragging)
                itemIcon.DOFade(1f, 0f); // Instant reset after drag
        }

        public void SetSelected(bool selected) => selectionBorder.enabled = selected;

        public void SetDragging(bool dragging)
        {
            _isDragging = dragging;
            itemIcon.DOFade(dragging ? 0.3f : 1f, 0.1f);
        }

        // ── Pointer Events ──
        public void OnPointerEnter(PointerEventData e)
        {
            if (_currentSlot != null && !_currentSlot.IsEmpty)
                _parent.ShowTooltip(_currentSlot.ItemId, transform.position);
            background.DOFade(0.5f, 0.1f);
        }

        public void OnPointerExit(PointerEventData e)
        {
            _parent.HideTooltip();
            background.DOFade(0.2f, 0.1f);
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Right)
                _parent.OnSlotRightClick(this);
        }

        // ── Drag Events ──
        public void OnBeginDrag(PointerEventData e) => _parent.OnBeginDrag(this, e);
        public void OnDrag(PointerEventData e) => _parent.OnDrag(e);
        public void OnEndDrag(PointerEventData e) => _parent.OnEndDrag(null, e);
        public void OnDrop(PointerEventData e) => _parent.OnEndDrag(this, e);
    }

    // ─────────────────────────────────────────────────────
    // ItemTooltip
    // ─────────────────────────────────────────────────────

    public class ItemTooltip : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI nameText;
        [SerializeField] private TextMeshProUGUI descText;
        [SerializeField] private TextMeshProUGUI rarityText;
        [SerializeField] private TextMeshProUGUI statsText;
        [SerializeField] private CanvasGroup group;
        [SerializeField] private RectTransform panel;

        private static readonly string[] RarityNames = { "Common","Uncommon","Rare","Epic","Legendary","Exclusive" };
        private static readonly Color[] RarityColors  =
        {
            Color.white,
            new Color(0.3f, 1f, 0.3f),
            new Color(0.3f, 0.5f, 1f),
            new Color(0.7f, 0.3f, 1f),
            new Color(1f, 0.8f, 0.1f),
            new Color(1f, 0.4f, 0.1f),
        };

        public void Show(ItemDefinition def, Vector3 position)
        {
            nameText.text = def.ItemName;
            nameText.color = RarityColors[(int)def.Rarity];
            descText.text  = def.Description;
            rarityText.text = RarityNames[(int)def.Rarity];
            rarityText.color = RarityColors[(int)def.Rarity];

            var stats = "";
            if (def.IsTool)    stats += $"⚒ Break Power: {def.BreakPower}\n";
            if (def.IsWeapon)  stats += $"⚔ Damage: {def.AttackDamage}\n";
            if (def.IsSeed)    stats += $"🌱 Growth: {FormatTime(def.GrowthTimeSeconds)}\n";
            if (def.IsPlaceable) stats += $"🧱 Durability: {def.Durability}\n";
            statsText.text = stats.TrimEnd();

            transform.position = ClampToScreen(position, panel);
            gameObject.SetActive(true);
            group.DOFade(1f, 0.15f);
        }

        public void Hide()
        {
            group.DOFade(0f, 0.1f).OnComplete(() => gameObject.SetActive(false));
        }

        private static Vector3 ClampToScreen(Vector3 pos, RectTransform rt)
        {
            float w = rt.rect.width  / 2f;
            float h = rt.rect.height / 2f;
            pos.x = Mathf.Clamp(pos.x, w,   Screen.width  - w);
            pos.y = Mathf.Clamp(pos.y, h,   Screen.height - h);
            return pos;
        }

        private static string FormatTime(float seconds)
        {
            if (seconds < 60)   return $"{seconds:F0}s";
            if (seconds < 3600) return $"{seconds / 60:F0}m";
            return $"{seconds / 3600:F1}h";
        }
    }

    // ─── Helpers ───

    public struct ContextMenuOption
    {
        public string Label;
        public System.Action Action;
        public ContextMenuOption(string label, System.Action action) { Label = label; Action = action; }
    }
}
