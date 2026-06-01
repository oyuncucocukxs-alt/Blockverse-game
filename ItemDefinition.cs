using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace BlockVerse.Items
{
    // ─────────────────────────────────────────────
    // Item Definition (ScriptableObject per item)
    // ─────────────────────────────────────────────

    [CreateAssetMenu(fileName = "Item", menuName = "BlockVerse/Item Definition")]
    public class ItemDefinition : ScriptableObject
    {
        [Header("Identity")]
        public int ItemId;
        public string ItemName;
        [TextArea(2, 4)]
        public string Description;
        public ItemType ItemType;
        public ItemRarity Rarity;

        [Header("Sprites")]
        public Sprite InventorySprite;
        public Sprite WorldSprite;     // dropped in world
        public GameObject WorldPrefab; // for drops with physics

        [Header("Stack & Economy")]
        public int MaxStack = 200;
        public int BaseSellPrice;  // in gems
        public int BaseBuyPrice;

        [Header("Block Properties")]
        public bool IsPlaceable;
        public bool IsBackgroundOnly;
        public bool HasCollision = true;
        public int Durability = 100;   // hit points when placed
        public int BreakDrop;          // itemId dropped when broken
        public int BreakDropCount = 1;
        public float BreakTime = 0.5f; // seconds to fully break

        [Header("Tool Properties")]
        public bool IsTool;
        public ToolType ToolType;
        public int BreakPower = 10;    // damage per hit
        public float ToolRange = 5f;

        [Header("Weapon Properties")]
        public bool IsWeapon;
        public int AttackDamage;
        public float AttackSpeed;
        public float AttackRange;

        [Header("Wearable Properties")]
        public bool IsWearable;
        public WearableSlot WearableSlot;
        public Sprite WornSprite;      // what it looks like on player

        [Header("Seed/Farming")]
        public bool IsSeed;
        public int GrowsIntoItemId;    // the tree/plant block it becomes
        public float GrowthTimeSeconds = 3600f;
        public int HarvestDropItemId;
        public int HarvestDropMin = 1;
        public int HarvestDropMax = 4;
        public int SeedDropFromHarvest = 1;

        [Header("Consumable")]
        public bool IsConsumable;
        public int HealAmount;
        public float SpeedBoostDuration;
        public float SpeedBoostMultiplier = 1f;

        [Header("Special Blocks")]
        public bool IsVendingMachine;
        public bool IsLock;            // world lock, access control
        public bool IsPortal;
        public bool IsDoor;
        public bool IsLiquid;

        [Header("Crafting")]
        public CraftingRecipe[] CraftingRecipes;

        [Header("Addressable")]
        public AssetReferenceSprite AddressableSprite;

        // ─────────────────────────────────────────────

        public bool IsPlaceableAs(bool background) =>
            IsPlaceable && (background == IsBackgroundOnly || !IsBackgroundOnly);
    }

    // ─────────────────────────────────────────────
    // Item Database (Addressables-based)
    // ─────────────────────────────────────────────

    public class ItemDatabase : MonoBehaviour
    {
        public static ItemDatabase Instance { get; private set; }

        private readonly Dictionary<int, ItemDefinition> _items = new();
        private readonly Dictionary<string, ItemDefinition> _itemsByName = new();

        private void Awake() => Instance = this;

        public IEnumerator LoadAllItems()
        {
            Debug.Log("[ItemDatabase] Loading items via Addressables...");

            var handle = Addressables.LoadAssetsAsync<ItemDefinition>("Items", null);
            yield return handle;

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Debug.LogError("[ItemDatabase] Failed to load items.");
                yield break;
            }

            foreach (var item in handle.Result)
            {
                _items[item.ItemId] = item;
                _itemsByName[item.ItemName.ToLower()] = item;
            }

            Addressables.Release(handle);
            Debug.Log($"[ItemDatabase] Loaded {_items.Count} items.");
        }

        public ItemDefinition GetItem(int itemId)
        {
            _items.TryGetValue(itemId, out var def);
            return def;
        }

        public ItemDefinition GetItem(string name)
        {
            _itemsByName.TryGetValue(name.ToLower(), out var def);
            return def;
        }

        public IEnumerable<ItemDefinition> GetAllItems() => _items.Values;

        public IEnumerable<ItemDefinition> GetItemsOfType(ItemType type)
        {
            foreach (var item in _items.Values)
                if (item.ItemType == type) yield return item;
        }
    }

    // ─────────────────────────────────────────────
    // Enums
    // ─────────────────────────────────────────────

    public enum ItemType
    {
        Block,
        Seed,
        Tool,
        Weapon,
        Wearable,
        Consumable,
        CraftingMaterial,
        Currency,
        Special
    }

    public enum ItemRarity
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary,
        Exclusive
    }

    public enum ToolType
    {
        None,
        Fist,
        Pickaxe,
        Axe,
        Shovel,
        Sword,
        Wrench,
        MagicWand
    }

    public enum WearableSlot
    {
        Head,
        Face,
        Neck,
        Shirt,
        Pants,
        Shoes,
        Hand,
        Back,
        Wing
    }

    // ─────────────────────────────────────────────
    // Crafting
    // ─────────────────────────────────────────────

    [Serializable]
    public class CraftingRecipe
    {
        public CraftingIngredient[] Ingredients;
        public int OutputItemId;
        public int OutputCount;
        public bool RequiresCraftingTable;
        public float CraftTime;
    }

    [Serializable]
    public struct CraftingIngredient
    {
        public int ItemId;
        public int Count;
    }
}
