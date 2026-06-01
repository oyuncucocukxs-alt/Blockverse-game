using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace BlockVerse.UI
{
    /// <summary>
    /// World browser: search, trending, owned worlds, create world UI.
    /// </summary>
    public class WorldSearchUI : MonoBehaviour
    {
        [Header("Search")]
        [SerializeField] private TMP_InputField searchField;
        [SerializeField] private Button         searchBtn;
        [SerializeField] private Button         clearBtn;

        [Header("Sort / Filter")]
        [SerializeField] private TMP_Dropdown   sortDropdown;
        [SerializeField] private Toggle         showLockedToggle;

        [Header("Tabs")]
        [SerializeField] private Button trendingTab;
        [SerializeField] private Button myWorldsTab;
        [SerializeField] private Button friendsTab;

        [Header("World List")]
        [SerializeField] private Transform        worldListContainer;
        [SerializeField] private WorldEntryUI     worldEntryPrefab;
        [SerializeField] private ScrollRect       scrollRect;
        [SerializeField] private GameObject       loadingIndicator;
        [SerializeField] private TextMeshProUGUI  noResultsText;

        [Header("Pagination")]
        [SerializeField] private Button prevPageBtn;
        [SerializeField] private Button nextPageBtn;
        [SerializeField] private TextMeshProUGUI pageText;

        [Header("Create World")]
        [SerializeField] private Button         createWorldBtn;
        [SerializeField] private GameObject     createWorldPanel;
        [SerializeField] private TMP_InputField newWorldNameField;
        [SerializeField] private Toggle         lockWorldToggle;
        [SerializeField] private Button         confirmCreateBtn;
        [SerializeField] private Button         cancelCreateBtn;
        [SerializeField] private TextMeshProUGUI createErrorText;
        [SerializeField] private TextMeshProUGUI worldNamePreview;

        private List<WorldEntryUI> _entries = new();
        private int    _currentPage  = 1;
        private int    _totalPages   = 1;
        private string _currentSort  = "visitCount";
        private string _activeTab    = "trending";
        private bool   _isLoading;

        private readonly string[] SortValues  = { "visitCount", "likeCount", "newest" };
        private readonly string[] SortLabels  = { "Most Visited", "Most Liked",  "Newest"    };

        private void Start()
        {
            searchBtn.onClick.AddListener(() => Search(1));
            clearBtn.onClick.AddListener(ClearSearch);
            searchField.onSubmit.AddListener(_ => Search(1));

            sortDropdown.ClearOptions();
            sortDropdown.AddOptions(new List<string>(SortLabels));
            sortDropdown.onValueChanged.AddListener(i =>
            {
                _currentSort = SortValues[i];
                Search(1);
            });

            trendingTab.onClick.AddListener(() => SwitchTab("trending"));
            myWorldsTab.onClick.AddListener(() => SwitchTab("my"));
            friendsTab.onClick.AddListener(() => SwitchTab("friends"));

            prevPageBtn.onClick.AddListener(() => Search(_currentPage - 1));
            nextPageBtn.onClick.AddListener(() => Search(_currentPage + 1));

            createWorldBtn.onClick.AddListener(() => createWorldPanel.SetActive(true));
            confirmCreateBtn.onClick.AddListener(OnCreateWorld);
            cancelCreateBtn.onClick.AddListener(() => createWorldPanel.SetActive(false));
            newWorldNameField.onValueChanged.AddListener(v =>
                worldNamePreview.text = v.ToUpper().Replace(" ", "_"));

            SwitchTab("trending");
        }

        // ─────────────────────────────────────────────
        #region Search & Load

        private void SwitchTab(string tab)
        {
            _activeTab   = tab;
            _currentPage = 1;
            SetTabHighlight(tab);
            Search(1);
        }

        private void SetTabHighlight(string tab)
        {
            void Set(Button b, bool active) {
                var img   = b.GetComponent<Image>();
                img.color = active ? new Color(0.3f, 0.6f, 1f) : new Color(0.2f, 0.2f, 0.2f);
            }
            Set(trendingTab, tab == "trending");
            Set(myWorldsTab, tab == "my");
            Set(friendsTab,  tab == "friends");
        }

        private void Search(int page)
        {
            if (_isLoading) return;
            page = Mathf.Max(1, page);
            _currentPage = page;
            StartCoroutine(LoadWorlds());
        }

        private void ClearSearch()
        {
            searchField.text = "";
            Search(1);
        }

        private IEnumerator LoadWorlds()
        {
            _isLoading = true;
            SetLoading(true);

            string query = searchField.text.Trim();
            WorldSearchResult result = null;

            if (_activeTab == "my")
            {
                yield return BackendClient.Instance.GetMyWorlds(
                    r => result = r, err => Debug.LogError(err));
            }
            else
            {
                yield return BackendClient.Instance.SearchWorlds(
                    query, _currentPage, _currentSort,
                    r => result = r, err => Debug.LogError(err));
            }

            SetLoading(false);
            _isLoading = false;

            if (result == null) { noResultsText.gameObject.SetActive(true); yield break; }

            _totalPages = Mathf.Max(1, result.TotalPages);
            BuildList(result.Worlds);
            UpdatePagination();
        }

        private void BuildList(List<WorldEntry> worlds)
        {
            // Clear old entries (return to pool)
            foreach (var e in _entries) Destroy(e.gameObject);
            _entries.Clear();

            noResultsText.gameObject.SetActive(worlds == null || worlds.Count == 0);
            if (worlds == null) return;

            for (int i = 0; i < worlds.Count; i++)
            {
                var entry = Instantiate(worldEntryPrefab, worldListContainer);
                entry.Setup(worlds[i], this);
                _entries.Add(entry);

                // Staggered fade-in
                var cg = entry.GetComponent<CanvasGroup>() ?? entry.gameObject.AddComponent<CanvasGroup>();
                cg.alpha = 0;
                float delay = i * 0.04f;
                cg.DOFade(1f, 0.2f).SetDelay(delay);
            }

            // Scroll to top
            scrollRect.verticalNormalizedPosition = 1f;
        }

        private void UpdatePagination()
        {
            pageText.text = $"{_currentPage} / {_totalPages}";
            prevPageBtn.interactable = _currentPage > 1;
            nextPageBtn.interactable = _currentPage < _totalPages;
        }

        private void SetLoading(bool active)
        {
            loadingIndicator.SetActive(active);
            if (active)
            {
                loadingIndicator.transform
                    .DORotate(new Vector3(0, 0, -360), 0.8f, RotateMode.FastBeyond360)
                    .SetLoops(-1).SetEase(Ease.Linear);
            }
            else
            {
                DOTween.Kill(loadingIndicator.transform);
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Join World

        public void JoinWorld(string worldId)
        {
            UIManager.Instance.CloseActivePanel();
            GameManager.Instance.JoinWorld(worldId);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Create World

        private void OnCreateWorld()
        {
            createErrorText.text = "";
            string name = newWorldNameField.text.Trim();

            if (name.Length < 1 || name.Length > 24)
            {
                createErrorText.text = "World name must be 1-24 characters."; return;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9 _-]+$"))
            {
                createErrorText.text = "Letters, numbers, spaces, _ - only."; return;
            }

            confirmCreateBtn.interactable = false;

            StartCoroutine(BackendClient.Instance.CreateWorld(
                name, lockWorldToggle.isOn,
                worldId =>
                {
                    confirmCreateBtn.interactable = true;
                    createWorldPanel.SetActive(false);
                    newWorldNameField.text = "";
                    JoinWorld(worldId);
                },
                err =>
                {
                    confirmCreateBtn.interactable = true;
                    createErrorText.text = err;
                }
            ));
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // World Entry Row UI
    // ─────────────────────────────────────────────────────

    public class WorldEntryUI : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI worldName;
        [SerializeField] private TextMeshProUGUI visitCount;
        [SerializeField] private TextMeshProUGUI playerCount;
        [SerializeField] private TextMeshProUGUI likeCount;
        [SerializeField] private Image           lockIcon;
        [SerializeField] private Image           onlineIndicator;
        [SerializeField] private Button          joinBtn;
        [SerializeField] private Button          likeBtn;

        private string _worldId;

        public void Setup(WorldEntry data, WorldSearchUI parent)
        {
            _worldId = data.WorldId;

            worldName.text   = data.Name;
            visitCount.text  = FormatNum(data.VisitCount) + " visits";
            playerCount.text = data.PlayerCount > 0 ? $"🟢 {data.PlayerCount} online" : "Empty";
            likeCount.text   = FormatNum(data.LikeCount);

            lockIcon.gameObject.SetActive(data.IsLocked);
            onlineIndicator.color = data.PlayerCount > 0
                ? new Color(0.2f, 1f, 0.2f)
                : new Color(0.5f, 0.5f, 0.5f);

            joinBtn.onClick.AddListener(() => parent.JoinWorld(data.WorldId));
            likeBtn.onClick.AddListener(() => StartCoroutine(
                BackendClient.Instance.LikeWorld(data.WorldId,
                    () => { likeBtn.interactable = false; },
                    _ => { })
            ));
        }

        private IEnumerator LikeWorld(string worldId)
        {
            yield return null; // placeholder
        }

        private string FormatNum(int n)
        {
            if (n >= 1_000_000) return $"{n / 1_000_000f:F1}M";
            if (n >= 1_000)     return $"{n / 1_000f:F1}K";
            return n.ToString();
        }
    }
}
