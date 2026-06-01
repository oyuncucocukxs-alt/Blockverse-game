using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace BlockVerse.Economy
{
    // ─────────────────────────────────────────────────────
    // Battle Pass Data
    // ─────────────────────────────────────────────────────

    [Serializable]
    public class BattlePassSeason
    {
        public int    SeasonId;
        public string SeasonName;
        public string Theme;
        public long   StartsAt;   // unix timestamp
        public long   EndsAt;
        public int    TotalTiers; // e.g. 100
        public int    XpPerTier;  // e.g. 1000
        public BattlePassTier[] FreeTiers;
        public BattlePassTier[] PremiumTiers;
    }

    [Serializable]
    public class BattlePassTier
    {
        public int    Tier;
        public int    RewardItemId;
        public int    RewardCount;
        public int    RewardCrystals;
        public string RewardName;
        public Sprite RewardSprite;
        public bool   IsGrandReward; // special animation for tier 100
    }

    [Serializable]
    public class BattlePassProgress
    {
        public int    SeasonId;
        public int    CurrentTier;
        public int    CurrentXp;
        public bool   IsPremium;
        public long   PurchasedAt;
        public List<int> ClaimedFreeTiers    = new();
        public List<int> ClaimedPremiumTiers = new();
    }

    // ─────────────────────────────────────────────────────
    // Battle Pass Manager (client)
    // ─────────────────────────────────────────────────────

    public class BattlePassManager : MonoBehaviour
    {
        public static BattlePassManager Instance { get; private set; }

        private BattlePassSeason   _currentSeason;
        private BattlePassProgress _myProgress;

        public BattlePassSeason   CurrentSeason  => _currentSeason;
        public BattlePassProgress MyProgress     => _myProgress;
        public bool               HasSeason      => _currentSeason != null;

        public event Action OnProgressUpdated;

        private void Awake() => Instance = this;

        public IEnumerator LoadCurrentSeason()
        {
            yield return BackendClient.Instance.GetBattlePassSeason(
                season   => _currentSeason = season,
                err      => Debug.LogWarning($"[BattlePass] {err}")
            );

            if (_currentSeason != null)
                yield return LoadMyProgress();
        }

        public IEnumerator LoadMyProgress()
        {
            yield return BackendClient.Instance.GetBattlePassProgress(
                p  => { _myProgress = p; OnProgressUpdated?.Invoke(); },
                _  => { }
            );
        }

        public void AddXp(int xp)
        {
            if (_myProgress == null || _currentSeason == null) return;

            _myProgress.CurrentXp += xp;
            int newTier = _myProgress.CurrentXp / _currentSeason.XpPerTier;
            if (newTier > _myProgress.CurrentTier)
            {
                int oldTier = _myProgress.CurrentTier;
                _myProgress.CurrentTier = Mathf.Min(newTier, _currentSeason.TotalTiers);
                OnTierUp(oldTier, _myProgress.CurrentTier);
            }

            OnProgressUpdated?.Invoke();
        }

        private void OnTierUp(int from, int to)
        {
            for (int tier = from + 1; tier <= to; tier++)
                NotificationPool.Show($"Battle Pass Tier {tier} Reached! 🎉", Color.yellow);

            AudioManager.Instance.PlaySfx("tier_up");
        }

        public IEnumerator ClaimReward(int tier, bool premium)
        {
            bool claimed = false;
            yield return BackendClient.Instance.ClaimBattlePassReward(
                _currentSeason.SeasonId, tier, premium,
                () =>
                {
                    claimed = true;
                    if (premium) _myProgress.ClaimedPremiumTiers.Add(tier);
                    else         _myProgress.ClaimedFreeTiers.Add(tier);
                    OnProgressUpdated?.Invoke();
                },
                err => UIManager.Instance.ShowError(err)
            );
        }

        public IEnumerator PurchasePremium()
        {
            if (_myProgress == null) yield break;
            yield return BackendClient.Instance.PurchaseBattlePass(
                _currentSeason.SeasonId,
                () =>
                {
                    _myProgress.IsPremium = true;
                    OnProgressUpdated?.Invoke();
                    UIManager.Instance.ShowNotification("Premium Battle Pass unlocked! 🌟", Color.yellow);
                },
                err => UIManager.Instance.ShowError(err)
            );
        }
    }

    // ─────────────────────────────────────────────────────
    // Battle Pass UI
    // ─────────────────────────────────────────────────────

    public class BattlePassUI : MonoBehaviour
    {
        [Header("Header")]
        [SerializeField] private TextMeshProUGUI seasonNameText;
        [SerializeField] private TextMeshProUGUI timerText;
        [SerializeField] private TextMeshProUGUI tierText;
        [SerializeField] private Slider          xpSlider;
        [SerializeField] private TextMeshProUGUI xpText;

        [Header("Premium")]
        [SerializeField] private Button          buyPremiumBtn;
        [SerializeField] private TextMeshProUGUI premiumPriceText;
        [SerializeField] private GameObject      premiumOwnedBadge;
        [SerializeField] private Image           premiumLockOverlay;

        [Header("Reward Track")]
        [SerializeField] private ScrollRect      trackScrollRect;
        [SerializeField] private Transform       trackContainer;
        [SerializeField] private BattlePassTierUI tierPrefab;
        [SerializeField] private RectTransform   currentTierIndicator;

        [Header("Close")]
        [SerializeField] private Button closeBtn;

        private List<BattlePassTierUI> _tierUIs = new();

        private void Start()
        {
            BattlePassManager.Instance.OnProgressUpdated += RefreshUI;
            buyPremiumBtn.onClick.AddListener(() =>
                StartCoroutine(BattlePassManager.Instance.PurchasePremium()));
            closeBtn.onClick.AddListener(() => UIManager.Instance.CloseActivePanel());
        }

        private void OnEnable()
        {
            StartCoroutine(Load());
        }

        private IEnumerator Load()
        {
            if (!BattlePassManager.Instance.HasSeason)
                yield return BattlePassManager.Instance.LoadCurrentSeason();

            BuildTrack();
            RefreshUI();
        }

        private void BuildTrack()
        {
            foreach (Transform t in trackContainer) Destroy(t.gameObject);
            _tierUIs.Clear();

            var season = BattlePassManager.Instance.CurrentSeason;
            if (season == null) return;

            seasonNameText.text = season.SeasonName;

            // Merge free + premium tiers by tier number
            for (int tier = 1; tier <= season.TotalTiers; tier++)
            {
                int t = tier;
                var freeTier    = Array.Find(season.FreeTiers,    x => x.Tier == t);
                var premTier    = Array.Find(season.PremiumTiers, x => x.Tier == t);

                var ui = Instantiate(tierPrefab, trackContainer);
                ui.Setup(tier, freeTier, premTier, this);
                _tierUIs.Add(ui);
            }
        }

        private void RefreshUI()
        {
            var season   = BattlePassManager.Instance.CurrentSeason;
            var progress = BattlePassManager.Instance.MyProgress;
            if (season == null || progress == null) return;

            tierText.text = $"Tier {progress.CurrentTier} / {season.TotalTiers}";

            int xpInTier = progress.CurrentXp % season.XpPerTier;
            xpSlider.value = (float)xpInTier / season.XpPerTier;
            xpText.text = $"{xpInTier} / {season.XpPerTier} XP";

            // Countdown timer
            var endsAt = DateTimeOffset.FromUnixTimeSeconds(season.EndsAt);
            var remaining = endsAt - DateTimeOffset.UtcNow;
            timerText.text = remaining.TotalDays >= 1
                ? $"{(int)remaining.TotalDays}d {remaining.Hours}h remaining"
                : $"{remaining.Hours}h {remaining.Minutes}m remaining";

            // Premium
            bool hasPremium = progress.IsPremium;
            buyPremiumBtn.gameObject.SetActive(!hasPremium);
            premiumOwnedBadge.SetActive(hasPremium);
            premiumLockOverlay.gameObject.SetActive(!hasPremium);

            // Refresh tier states
            foreach (var ui in _tierUIs)
                ui.RefreshState(progress);

            // Scroll to current tier
            ScrollToTier(progress.CurrentTier);
        }

        private void ScrollToTier(int tier)
        {
            if (_tierUIs.Count == 0) return;
            tier = Mathf.Clamp(tier, 1, _tierUIs.Count);
            float normalizedPos = (float)(tier - 1) / (_tierUIs.Count - 1);
            trackScrollRect.DOHorizontalNormalizedPos(normalizedPos, 0.4f).SetEase(Ease.OutQuad);
        }

        public void ClaimTier(int tier, bool premium)
        {
            StartCoroutine(BattlePassManager.Instance.ClaimReward(tier, premium));
        }
    }

    // ─────────────────────────────────────────────────────
    // Tier UI Cell
    // ─────────────────────────────────────────────────────

    public class BattlePassTierUI : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI tierLabel;
        [SerializeField] private Image           freeIcon;
        [SerializeField] private Image           premIcon;
        [SerializeField] private TextMeshProUGUI freeName;
        [SerializeField] private TextMeshProUGUI premName;
        [SerializeField] private Button          freeClaimBtn;
        [SerializeField] private Button          premClaimBtn;
        [SerializeField] private Image           freeClaimedMark;
        [SerializeField] private Image           premClaimedMark;
        [SerializeField] private Image           tierHighlight;
        [SerializeField] private GameObject      lockIcon;
        [SerializeField] private ParticleSystem  claimParticles;

        private int          _tier;
        private BattlePassUI _parent;

        public void Setup(int tier, BattlePassTier free, BattlePassTier premium, BattlePassUI parent)
        {
            _tier   = tier;
            _parent = parent;

            tierLabel.text = tier.ToString();

            if (free != null)
            {
                freeIcon.sprite = free.RewardSprite;
                freeName.text   = free.RewardName;
                freeClaimBtn.onClick.AddListener(() => parent.ClaimTier(tier, false));
            }
            else freeIcon.transform.parent.gameObject.SetActive(false);

            if (premium != null)
            {
                premIcon.sprite = premium.RewardSprite;
                premName.text   = premium.RewardName;
                premClaimBtn.onClick.AddListener(() => parent.ClaimTier(tier, true));
            }
            else premIcon.transform.parent.gameObject.SetActive(false);
        }

        public void RefreshState(BattlePassProgress progress)
        {
            bool reached       = progress.CurrentTier >= _tier;
            bool freeClaimed   = progress.ClaimedFreeTiers.Contains(_tier);
            bool premClaimed   = progress.ClaimedPremiumTiers.Contains(_tier);

            // Highlight current tier
            tierHighlight.color = _tier == progress.CurrentTier
                ? new Color(1f, 0.8f, 0.1f, 0.8f)
                : new Color(0.15f, 0.15f, 0.15f, 0.8f);

            freeClaimBtn.interactable  = reached && !freeClaimed;
            freeClaimedMark.enabled    = freeClaimed;

            premClaimBtn.interactable  = reached && !premClaimed && progress.IsPremium;
            premClaimedMark.enabled    = premClaimed;
            lockIcon.SetActive(!progress.IsPremium);
        }
    }
}
