using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Extension;
using TMPro;
using DG.Tweening;
using BlockVerse.Network;

namespace BlockVerse.Economy
{
    /// <summary>
    /// Handles premium currency (gems/crystals), IAP via Unity IAP,
    /// and the cosmetic shop UI.
    /// </summary>

    // ─────────────────────────────────────────────────────
    // Currency System (client-side display)
    // ─────────────────────────────────────────────────────

    public class CurrencySystem : MonoBehaviour
    {
        public static CurrencySystem Instance { get; private set; }

        public const int GEM_ITEM_ID     = 1001;
        public const int CRYSTAL_ITEM_ID = 1002; // premium currency

        [SerializeField] private TextMeshProUGUI gemsDisplay;
        [SerializeField] private TextMeshProUGUI crystalsDisplay;
        [SerializeField] private Animator gemsAnimator;

        private int _gems;
        private int _crystals;

        public int Gems     => _gems;
        public int Crystals => _crystals;

        public event Action<int, int> OnCurrencyChanged; // (newGems, newCrystals)

        private void Awake()
        {
            Instance = this;
            NetworkClient.RegisterHandler<CurrencyUpdateMessage>(OnCurrencyUpdate);
        }

        public void Initialize(int gems, int crystals)
        {
            _gems     = gems;
            _crystals = crystals;
            RefreshDisplay(false);
        }

        private void OnCurrencyUpdate(CurrencyUpdateMessage msg)
        {
            bool gemsIncreased = msg.Gems > _gems;
            _gems     = msg.Gems;
            _crystals = msg.Crystals;
            RefreshDisplay(gemsIncreased);
            OnCurrencyChanged?.Invoke(_gems, _crystals);
        }

        private void RefreshDisplay(bool animate)
        {
            gemsDisplay.text     = FormatCurrency(_gems);
            crystalsDisplay.text = FormatCurrency(_crystals);

            if (animate)
            {
                gemsDisplay.transform.DOPunchScale(Vector3.one * 0.2f, 0.3f, 5, 0.5f);
                gemsAnimator?.SetTrigger("Bounce");
            }
        }

        private static string FormatCurrency(int amount)
        {
            if (amount >= 1_000_000) return $"{amount / 1_000_000f:F1}M";
            if (amount >= 1_000)     return $"{amount / 1_000f:F1}K";
            return amount.ToString("N0");
        }
    }

    public struct CurrencyUpdateMessage : Mirror.NetworkMessage
    {
        public int Gems;
        public int Crystals;
    }

    // ─────────────────────────────────────────────────────
    // IAP Manager (Unity Purchasing)
    // ─────────────────────────────────────────────────────

    public class IAPManager : MonoBehaviour, IDetailedStoreListener
    {
        public static IAPManager Instance { get; private set; }

        private IStoreController   _storeController;
        private IExtensionProvider _extensions;

        // IAP Product IDs (must match App Store / Play Store)
        private static readonly Dictionary<string, int> ProductCrystals = new()
        {
            { "blockverse.crystals.80",    80    },
            { "blockverse.crystals.500",   500   },
            { "blockverse.crystals.1200",  1200  },
            { "blockverse.crystals.2800",  2800  },
            { "blockverse.crystals.8000",  8000  },
            { "blockverse.crystals.20000", 20000 },
        };

        private static readonly Dictionary<string, string> ProductDisplayPrices = new()
        {
            { "blockverse.crystals.80",    "$0.99"  },
            { "blockverse.crystals.500",   "$4.99"  },
            { "blockverse.crystals.1200",  "$9.99"  },
            { "blockverse.crystals.2800",  "$19.99" },
            { "blockverse.crystals.8000",  "$49.99" },
            { "blockverse.crystals.20000", "$99.99" },
        };

        public event Action<string, bool> OnPurchaseComplete; // productId, success

        private void Awake()
        {
            Instance = this;
            InitializePurchasing();
        }

        private void InitializePurchasing()
        {
            var builder = ConfigurationBuilder.Instance(StandardPurchasingModule.Instance());

            foreach (var productId in ProductCrystals.Keys)
                builder.AddProduct(productId, ProductType.Consumable);

            UnityPurchasing.Initialize(this, builder);
        }

        public void OnInitialized(IStoreController controller, IExtensionProvider extensions)
        {
            _storeController = controller;
            _extensions      = extensions;
            Debug.Log("[IAP] Store initialized.");
        }

        public void OnInitializeFailed(InitializationFailureReason error)
            => Debug.LogError($"[IAP] Init failed: {error}");

        public void OnInitializeFailed(InitializationFailureReason error, string message)
            => Debug.LogError($"[IAP] Init failed: {error} - {message}");

        public void BuyProduct(string productId)
        {
            if (_storeController == null) { Debug.LogError("[IAP] Store not initialized."); return; }
            _storeController.InitiatePurchase(productId);
        }

        public PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs args)
        {
            string productId = args.purchasedProduct.definition.id;

            // Validate receipt with backend
            StartCoroutine(ValidateAndGrantCrystals(productId, args.purchasedProduct.receipt));

            return PurchaseProcessingResult.Pending; // Complete after backend validation
        }

        private IEnumerator ValidateAndGrantCrystals(string productId, string receipt)
        {
            bool success = false;
            yield return BackendClient.Instance.ValidateIAPReceipt(
                productId, receipt,
                result => success = result,
                err => Debug.LogError($"[IAP] Validation error: {err}")
            );

            if (success)
            {
                _storeController.ConfirmPendingPurchase(
                    _storeController.products.WithID(productId));
                OnPurchaseComplete?.Invoke(productId, true);
                UIManager.Instance.ShowNotification(
                    $"+{ProductCrystals[productId]} Crystals!", Color.cyan);
            }
            else
            {
                OnPurchaseComplete?.Invoke(productId, false);
                UIManager.Instance.ShowError("Purchase validation failed. Contact support.");
            }
        }

        public void OnPurchaseFailed(Product product, PurchaseFailureReason failureReason)
        {
            Debug.LogWarning($"[IAP] Purchase failed: {product.definition.id} - {failureReason}");
            OnPurchaseComplete?.Invoke(product.definition.id, false);

            if (failureReason != PurchaseFailureReason.UserCancelled)
                UIManager.Instance.ShowError($"Purchase failed: {failureReason}");
        }

        public void OnPurchaseFailed(Product product, PurchaseFailureDescription failureDescription)
            => OnPurchaseFailed(product, PurchaseFailureReason.Unknown);

        public string GetLocalizedPrice(string productId)
        {
            if (_storeController == null) return ProductDisplayPrices.GetValueOrDefault(productId, "?");
            var product = _storeController.products.WithID(productId);
            return product?.metadata.localizedPriceString ?? ProductDisplayPrices.GetValueOrDefault(productId, "?");
        }
    }

    // ─────────────────────────────────────────────────────
    // Cosmetic Shop UI
    // ─────────────────────────────────────────────────────

    public class ShopUI : MonoBehaviour
    {
        [Header("Crystal Packs")]
        [SerializeField] private Transform crystalPackContainer;
        [SerializeField] private CrystalPackUI crystalPackPrefab;

        [Header("Cosmetic Items")]
        [SerializeField] private Transform cosmeticContainer;
        [SerializeField] private CosmeticItemUI cosmeticItemPrefab;
        [SerializeField] private ScrollRect cosmeticScrollRect;

        [Header("Tabs")]
        [SerializeField] private Button featuredTab;
        [SerializeField] private Button wearablesTab;
        [SerializeField] private Button emoteTab;
        [SerializeField] private Button crystalTab;

        [Header("Currency Display")]
        [SerializeField] private TextMeshProUGUI crystalBalanceText;
        [SerializeField] private TextMeshProUGUI gemBalanceText;

        [Header("Featured Banner")]
        [SerializeField] private Image featuredBanner;
        [SerializeField] private TextMeshProUGUI featuredItemName;
        [SerializeField] private TextMeshProUGUI featuredTimer;
        [SerializeField] private Button featuredBuyBtn;

        private List<ShopItemData> _shopItems = new();
        private ShopTab _activeTab = ShopTab.Featured;

        private void Start()
        {
            CurrencySystem.Instance.OnCurrencyChanged += OnCurrencyChanged;
            IAPManager.Instance.OnPurchaseComplete    += OnIAPComplete;

            featuredTab.onClick.AddListener(() => SwitchTab(ShopTab.Featured));
            wearablesTab.onClick.AddListener(() => SwitchTab(ShopTab.Wearables));
            emoteTab.onClick.AddListener(() => SwitchTab(ShopTab.Emotes));
            crystalTab.onClick.AddListener(() => SwitchTab(ShopTab.Crystals));

            BuildCrystalPacks();
            StartCoroutine(LoadShopItems());
            RefreshCurrencyDisplay();
        }

        private void RefreshCurrencyDisplay()
        {
            crystalBalanceText.text = CurrencySystem.Instance.Crystals.ToString("N0");
            gemBalanceText.text     = CurrencySystem.Instance.Gems.ToString("N0");
        }

        private void OnCurrencyChanged(int gems, int crystals)
        {
            crystalBalanceText.text = crystals.ToString("N0");
            gemBalanceText.text     = gems.ToString("N0");
            crystalBalanceText.transform.DOPunchScale(Vector3.one * 0.2f, 0.3f);
        }

        private void BuildCrystalPacks()
        {
            var packs = new[]
            {
                ("blockverse.crystals.80",    "Starter Pack",  80,    "assets/shop/pack_80"),
                ("blockverse.crystals.500",   "Explorer Pack", 500,   "assets/shop/pack_500"),
                ("blockverse.crystals.1200",  "Builder Pack",  1200,  "assets/shop/pack_1200"),
                ("blockverse.crystals.2800",  "Pro Pack",      2800,  "assets/shop/pack_2800"),
                ("blockverse.crystals.8000",  "Elite Pack",    8000,  "assets/shop/pack_8000"),
                ("blockverse.crystals.20000", "Legend Pack",   20000, "assets/shop/pack_20000"),
            };

            foreach (var (id, name, crystals, icon) in packs)
            {
                var ui = Instantiate(crystalPackPrefab, crystalPackContainer);
                ui.Setup(id, name, crystals,
                    IAPManager.Instance.GetLocalizedPrice(id),
                    () => IAPManager.Instance.BuyProduct(id));
            }
        }

        private IEnumerator LoadShopItems()
        {
            yield return BackendClient.Instance.GetShopItems(
                items => _shopItems = items,
                err   => Debug.LogError($"[Shop] Load error: {err}")
            );
            BuildCosmeticGrid(_activeTab);
        }

        private void BuildCosmeticGrid(ShopTab tab)
        {
            foreach (Transform t in cosmeticContainer) Destroy(t.gameObject);

            foreach (var item in _shopItems)
            {
                bool show = tab == ShopTab.Featured  ? item.IsFeatured :
                            tab == ShopTab.Wearables ? item.Type == "wearable" :
                            tab == ShopTab.Emotes    ? item.Type == "emote" : false;
                if (!show) continue;

                var ui = Instantiate(cosmeticItemPrefab, cosmeticContainer);
                ui.Setup(item, () => BuyCosmeticItem(item));
            }
        }

        private void BuyCosmeticItem(ShopItemData item)
        {
            if (CurrencySystem.Instance.Crystals < item.PriceCrystals)
            {
                SwitchTab(ShopTab.Crystals);
                UIManager.Instance.ShowNotification("Not enough crystals! Get more below.", Color.yellow);
                return;
            }

            ConfirmDialog.Show(
                "Purchase",
                $"Buy {item.Name} for {item.PriceCrystals} crystals?",
                "Buy",
                () => StartCoroutine(BackendClient.Instance.PurchaseShopItem(
                    item.ShopItemId,
                    () => UIManager.Instance.ShowNotification($"Purchased {item.Name}!", Color.green),
                    err => UIManager.Instance.ShowError(err)
                ))
            );
        }

        private void SwitchTab(ShopTab tab)
        {
            _activeTab = tab;
            crystalPackContainer.gameObject.SetActive(tab == ShopTab.Crystals);
            cosmeticContainer.gameObject.SetActive(tab != ShopTab.Crystals);

            if (tab != ShopTab.Crystals)
                BuildCosmeticGrid(tab);
        }

        private void OnIAPComplete(string productId, bool success)
        {
            RefreshCurrencyDisplay();
        }

        private void Update()
        {
            // Update featured item countdown timer
            // TODO: populate from server limited item expiry
        }

        private enum ShopTab { Featured, Wearables, Emotes, Crystals }
    }

    public class CrystalPackUI : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI packName;
        [SerializeField] private TextMeshProUGUI crystalCount;
        [SerializeField] private TextMeshProUGUI priceText;
        [SerializeField] private Button buyBtn;
        [SerializeField] private TextMeshProUGUI bonusLabel;

        public void Setup(string productId, string name, int crystals, string price, Action onBuy)
        {
            packName.text     = name;
            crystalCount.text = $"{crystals:N0} 💎";
            priceText.text    = price;
            buyBtn.onClick.AddListener(() => onBuy());

            // Bonus labels for larger packs
            bonusLabel.gameObject.SetActive(crystals >= 1200);
            if (crystals >= 8000)  bonusLabel.text = "BEST VALUE";
            else if (crystals >= 2800) bonusLabel.text = "POPULAR";
            else if (crystals >= 1200) bonusLabel.text = "+BONUS";
        }
    }

    public class CosmeticItemUI : MonoBehaviour
    {
        [SerializeField] private Image itemPreview;
        [SerializeField] private TextMeshProUGUI itemName;
        [SerializeField] private TextMeshProUGUI priceText;
        [SerializeField] private Image rarityBorder;
        [SerializeField] private Button buyBtn;
        [SerializeField] private GameObject limitedTag;
        [SerializeField] private GameObject newTag;

        public void Setup(ShopItemData item, Action onBuy)
        {
            itemName.text = item.Name;
            priceText.text = $"{item.PriceCrystals} 💎";
            limitedTag.SetActive(item.IsLimited);
            newTag.SetActive(item.IsNew);
            buyBtn.onClick.AddListener(() => onBuy());

            // Load preview via Addressables
            if (!string.IsNullOrEmpty(item.PreviewAddress))
                StartCoroutine(LoadPreview(item.PreviewAddress));
        }

        private IEnumerator LoadPreview(string address)
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>(address);
            yield return handle;
            if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded)
                itemPreview.sprite = handle.Result;
        }
    }

    [Serializable]
    public class ShopItemData
    {
        public string ShopItemId;
        public string Name;
        public string Description;
        public string Type;        // wearable, emote, etc.
        public int    GameItemId;
        public int    PriceCrystals;
        public int    PriceGems;
        public bool   IsFeatured;
        public bool   IsLimited;
        public bool   IsNew;
        public string PreviewAddress; // Addressables key
        public long   ExpiresAt;
    }
}
