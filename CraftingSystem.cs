using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using BlockVerse.Inventory;
using BlockVerse.Items;
using BlockVerse.Network;

namespace BlockVerse.Crafting
{
    /// <summary>
    /// Client-side crafting UI + server-validated crafting execution.
    /// All crafting is validated server-side; client shows prediction only.
    /// </summary>
    public class CraftingSystem : MonoBehaviour
    {
        public static CraftingSystem Instance { get; private set; }

        [Header("UI")]
        [SerializeField] private Transform recipeListContainer;
        [SerializeField] private CraftingRecipeUI recipePrefab;
        [SerializeField] private CraftingDetailPanel detailPanel;
        [SerializeField] private TMP_InputField searchField;
        [SerializeField] private GameObject noResultsLabel;

        private List<CraftingRecipeUI> _recipeUIs = new();
        private CraftingRecipe _selectedRecipe;
        private bool _nearCraftingTable;

        private void Awake()
        {
            Instance = this;
            NetworkClient.RegisterHandler<CraftingResultMessage>(OnCraftingResult);
        }

        private void Start()
        {
            searchField.onValueChanged.AddListener(FilterRecipes);
            BuildRecipeList();
        }

        // ─────────────────────────────────────────────
        #region Recipe List

        private void BuildRecipeList()
        {
            // Gather all recipes from item database
            var allRecipes = new List<(ItemDefinition item, CraftingRecipe recipe)>();

            foreach (var itemDef in ItemDatabase.Instance.GetAllItems())
            {
                if (itemDef.CraftingRecipes == null) continue;
                foreach (var recipe in itemDef.CraftingRecipes)
                    allRecipes.Add((itemDef, recipe));
            }

            // Sort: available first, then alphabetical
            allRecipes.Sort((a, b) =>
            {
                bool aAvail = CanCraft(a.recipe);
                bool bAvail = CanCraft(b.recipe);
                if (aAvail != bAvail) return bAvail.CompareTo(aAvail);
                return string.Compare(a.item.ItemName, b.item.ItemName, StringComparison.Ordinal);
            });

            foreach (var (item, recipe) in allRecipes)
            {
                var ui = Instantiate(recipePrefab, recipeListContainer);
                ui.Initialize(item, recipe, this);
                _recipeUIs.Add(ui);
            }
        }

        public void RefreshAll()
        {
            foreach (var ui in _recipeUIs)
                ui.RefreshAvailability();
        }

        private void FilterRecipes(string query)
        {
            int visible = 0;
            foreach (var ui in _recipeUIs)
            {
                bool match = string.IsNullOrEmpty(query) ||
                             ui.ItemName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                ui.gameObject.SetActive(match);
                if (match) visible++;
            }
            noResultsLabel.SetActive(visible == 0);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Craft Execution

        public void SelectRecipe(CraftingRecipe recipe, ItemDefinition outputItem)
        {
            _selectedRecipe = recipe;
            detailPanel.Show(recipe, outputItem, this);
        }

        public bool CanCraft(CraftingRecipe recipe)
        {
            if (recipe.RequiresCraftingTable && !_nearCraftingTable) return false;
            foreach (var ing in recipe.Ingredients)
            {
                if (!InventoryManager.Instance.HasItem(ing.ItemId, ing.Count))
                    return false;
            }
            return true;
        }

        public void CraftSelected(int times = 1)
        {
            if (_selectedRecipe == null) return;
            if (!CanCraft(_selectedRecipe)) return;

            // Clamp to max craftable
            int maxTimes = GetMaxCraftable(_selectedRecipe);
            times = Mathf.Clamp(times, 1, maxTimes);

            NetworkClient.Send(new CraftRequestMessage
            {
                OutputItemId = _selectedRecipe.OutputItemId,
                Times = times
            });

            // Optimistic UI disable while waiting for server response
            detailPanel.SetCrafting(true);
        }

        public void CraftMax()
        {
            int max = GetMaxCraftable(_selectedRecipe);
            CraftSelected(max);
        }

        public int GetMaxCraftable(CraftingRecipe recipe)
        {
            if (recipe == null) return 0;
            int max = int.MaxValue;
            foreach (var ing in recipe.Ingredients)
            {
                int owned = InventoryManager.Instance.GetItemCount(ing.ItemId);
                max = Mathf.Min(max, owned / ing.Count);
            }
            return max == int.MaxValue ? 0 : max;
        }

        private void OnCraftingResult(CraftingResultMessage msg)
        {
            detailPanel.SetCrafting(false);

            if (msg.Success)
            {
                UIManager.Instance.ShowNotification(
                    $"+{msg.OutputCount}x {ItemDatabase.Instance.GetItem(msg.OutputItemId)?.ItemName ?? "Item"}",
                    Color.green
                );
                AudioManager.Instance.PlaySfx("craft_success");
                detailPanel.PlaySuccessEffect();
                RefreshAll();
            }
            else
            {
                UIManager.Instance.ShowError(msg.ErrorMessage ?? "Crafting failed.");
                AudioManager.Instance.PlaySfx("error");
            }
        }

        public void SetNearCraftingTable(bool near)
        {
            _nearCraftingTable = near;
            RefreshAll();
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Crafting Detail Panel
    // ─────────────────────────────────────────────────────

    public class CraftingDetailPanel : MonoBehaviour
    {
        [SerializeField] private Image outputIcon;
        [SerializeField] private TextMeshProUGUI outputName;
        [SerializeField] private TextMeshProUGUI outputCount;
        [SerializeField] private TextMeshProUGUI craftTimeText;
        [SerializeField] private Transform ingredientContainer;
        [SerializeField] private CraftingIngredientUI ingredientPrefab;
        [SerializeField] private Button craftBtn;
        [SerializeField] private Button craftMaxBtn;
        [SerializeField] private Slider craftCountSlider;
        [SerializeField] private TextMeshProUGUI craftCountLabel;
        [SerializeField] private TextMeshProUGUI requiresTableLabel;
        [SerializeField] private ParticleSystem successParticles;

        private CraftingSystem _system;
        private CraftingRecipe _currentRecipe;
        private int _craftCount = 1;

        public void Show(CraftingRecipe recipe, ItemDefinition outputItem, CraftingSystem system)
        {
            _system = system;
            _currentRecipe = recipe;
            gameObject.SetActive(true);

            outputIcon.sprite  = outputItem.InventorySprite;
            outputName.text    = outputItem.ItemName;
            outputCount.text   = $"x{recipe.OutputCount}";
            craftTimeText.text = recipe.CraftTime > 0 ? $"{recipe.CraftTime}s" : "Instant";
            requiresTableLabel.gameObject.SetActive(recipe.RequiresCraftingTable);

            // Build ingredient list
            foreach (Transform t in ingredientContainer) Destroy(t.gameObject);
            foreach (var ing in recipe.Ingredients)
            {
                var ui = Instantiate(ingredientPrefab, ingredientContainer);
                ui.Setup(ing.ItemId, ing.Count);
            }

            // Craft count slider
            int maxCraft = system.GetMaxCraftable(recipe);
            craftCountSlider.maxValue = Mathf.Max(1, maxCraft);
            craftCountSlider.value = 1;
            _craftCount = 1;
            UpdateCraftCount(1);

            craftCountSlider.onValueChanged.RemoveAllListeners();
            craftCountSlider.onValueChanged.AddListener(v => UpdateCraftCount((int)v));

            craftBtn.onClick.RemoveAllListeners();
            craftBtn.onClick.AddListener(() => system.CraftSelected(_craftCount));

            craftMaxBtn.onClick.RemoveAllListeners();
            craftMaxBtn.onClick.AddListener(system.CraftMax);

            craftBtn.interactable = system.CanCraft(recipe);
        }

        private void UpdateCraftCount(int count)
        {
            _craftCount = count;
            craftCountLabel.text = count.ToString();
            craftBtn.interactable = _system.CanCraft(_currentRecipe) && count >= 1;
        }

        public void SetCrafting(bool active)
        {
            craftBtn.interactable = !active;
            craftMaxBtn.interactable = !active;
        }

        public void PlaySuccessEffect()
        {
            successParticles?.Play();
            outputIcon.transform.DOPunchScale(Vector3.one * 0.3f, 0.4f, 5, 0.5f);
        }
    }

    // ─────────────────────────────────────────────────────
    // CraftingRecipeUI (list item)
    // ─────────────────────────────────────────────────────

    public class CraftingRecipeUI : MonoBehaviour
    {
        [SerializeField] private Image icon;
        [SerializeField] private TextMeshProUGUI nameLabel;
        [SerializeField] private TextMeshProUGUI countLabel;
        [SerializeField] private Image availableIndicator;
        [SerializeField] private Button button;
        [SerializeField] private Image lockIcon;

        private ItemDefinition _itemDef;
        private CraftingRecipe _recipe;
        private CraftingSystem _system;

        public string ItemName => _itemDef?.ItemName ?? "";

        public void Initialize(ItemDefinition itemDef, CraftingRecipe recipe, CraftingSystem system)
        {
            _itemDef = itemDef;
            _recipe  = recipe;
            _system  = system;

            icon.sprite     = itemDef.InventorySprite;
            nameLabel.text  = itemDef.ItemName;
            countLabel.text = $"x{recipe.OutputCount}";
            lockIcon.gameObject.SetActive(recipe.RequiresCraftingTable);

            button.onClick.AddListener(() => system.SelectRecipe(recipe, itemDef));
            RefreshAvailability();
        }

        public void RefreshAvailability()
        {
            bool canCraft = _system.CanCraft(_recipe);
            availableIndicator.color = canCraft ? Color.green : Color.gray;
            nameLabel.color = canCraft ? Color.white : new Color(0.6f, 0.6f, 0.6f);
        }
    }

    // ─────────────────────────────────────────────────────
    // CraftingIngredientUI
    // ─────────────────────────────────────────────────────

    public class CraftingIngredientUI : MonoBehaviour
    {
        [SerializeField] private Image icon;
        [SerializeField] private TextMeshProUGUI countText;
        [SerializeField] private Image background;

        public void Setup(int itemId, int required)
        {
            var def   = ItemDatabase.Instance.GetItem(itemId);
            int owned = InventoryManager.Instance.GetItemCount(itemId);

            icon.sprite   = def?.InventorySprite;
            countText.text = $"{owned}/{required}";
            bool hasEnough = owned >= required;
            countText.color    = hasEnough ? Color.white : Color.red;
            background.color   = hasEnough ? new Color(0.2f, 0.5f, 0.2f, 0.5f)
                                           : new Color(0.5f, 0.2f, 0.2f, 0.5f);
        }
    }

    // ─────────────────────────────────────────────────────
    // Network Messages
    // ─────────────────────────────────────────────────────

    public struct CraftRequestMessage : Mirror.NetworkMessage
    {
        public int OutputItemId;
        public int Times;
    }

    public struct CraftingResultMessage : Mirror.NetworkMessage
    {
        public bool Success;
        public int  OutputItemId;
        public int  OutputCount;
        public string ErrorMessage;
    }

    // ─────────────────────────────────────────────────────
    // Server-side Crafting Processor
    // ─────────────────────────────────────────────────────

    public static class CraftingProcessor
    {
        /// <summary>Validates and executes a craft on the server.</summary>
        public static CraftingResultMessage Process(
            PlayerServerState player,
            CraftRequestMessage request)
        {
            // Find recipe
            CraftingRecipe recipe = null;
            ItemDefinition outputItem = null;

            foreach (var item in ItemDatabase.Instance.GetAllItems())
            {
                if (item.CraftingRecipes == null) continue;
                var match = Array.Find(item.CraftingRecipes,
                    r => r.OutputItemId == request.OutputItemId);
                if (match != null) { recipe = match; outputItem = item; break; }
            }

            if (recipe == null)
                return Fail("Unknown recipe.");

            // Cap times
            int times = Mathf.Clamp(request.Times, 1, 100);

            // Validate ingredients (for `times` crafts)
            foreach (var ing in recipe.Ingredients)
            {
                int required = ing.Count * times;
                if (!player.Inventory.HasItem(ing.ItemId, required))
                    return Fail($"Need {required}x {ItemDatabase.Instance.GetItem(ing.ItemId)?.ItemName}.");
            }

            // Check crafting table proximity if required
            if (recipe.RequiresCraftingTable)
            {
                // Server checks if player is near a crafting table tile
                if (!IsNearCraftingTable(player))
                    return Fail("Must be near a Crafting Table.");
            }

            // Consume ingredients
            foreach (var ing in recipe.Ingredients)
                player.Inventory.RemoveItem(ing.ItemId, ing.Count * times);

            // Give output
            int totalOutput = recipe.OutputCount * times;
            bool added = player.Inventory.AddItem(recipe.OutputItemId, totalOutput);

            if (!added)
            {
                // Inventory full — refund
                foreach (var ing in recipe.Ingredients)
                    player.Inventory.AddItem(ing.ItemId, ing.Count * times);
                return Fail("Inventory full.");
            }

            return new CraftingResultMessage
            {
                Success = true,
                OutputItemId = recipe.OutputItemId,
                OutputCount = totalOutput
            };
        }

        private static bool IsNearCraftingTable(PlayerServerState player)
        {
            // Check 5x5 area around player for crafting table tile (itemId = defined constant)
            const int CRAFTING_TABLE_ITEM_ID = 50;
            var worldEngine = WorldEngine_Server.Instance;
            if (worldEngine == null) return false;

            for (int dx = -3; dx <= 3; dx++)
            for (int dy = -3; dy <= 3; dy++)
            {
                int tx = Mathf.RoundToInt(player.Position.x) + dx;
                int ty = Mathf.RoundToInt(player.Position.y) + dy;
                var tile = worldEngine.GetTileAt(tx, ty, false);
                if (tile.ItemId == CRAFTING_TABLE_ITEM_ID) return true;
            }
            return false;
        }

        private static CraftingResultMessage Fail(string msg) =>
            new() { Success = false, ErrorMessage = msg };
    }
}
