using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace BlockVerse.Core
{
    /// <summary>
    /// Central game manager — singleton, persists across scenes.
    /// Manages boot sequence, scene transitions, and core system references.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("Configuration")]
        [SerializeField] private AppConfig config;

        [Header("System References")]
        public NetworkGameManager NetworkManager;
        public WorldEngine WorldEngine;
        public InventoryManager InventoryManager;
        public UIManager UIManager;
        public AudioManager AudioManager;

        public GameState CurrentState { get; private set; } = GameState.Booting;
        public PlayerData LocalPlayer { get; private set; }
        public bool IsInitialized { get; private set; }

        public event Action<GameState> OnGameStateChanged;

        // ─────────────────────────────────────────────
        #region Unity Lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
        }

        private IEnumerator Start()
        {
            yield return StartCoroutine(BootSequence());
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Boot Sequence

        private IEnumerator BootSequence()
        {
            Debug.Log("[BlockVerse] Boot sequence started.");
            SetState(GameState.Booting);

            // 1. Load Addressables catalog
            yield return StartCoroutine(InitializeAddressables());

            // 2. Load item database
            yield return StartCoroutine(ItemDatabase.Instance.LoadAllItems());

            // 3. Initialize audio
            AudioManager.Initialize();

            // 4. Check authentication
            bool authenticated = false;
            yield return StartCoroutine(AuthService.Instance.TryAutoLogin(result => authenticated = result));

            if (authenticated)
            {
                yield return StartCoroutine(LoadPlayerData());
                SetState(GameState.MainMenu);
            }
            else
            {
                SetState(GameState.Authentication);
            }

            IsInitialized = true;
            Debug.Log("[BlockVerse] Boot complete.");
        }

        private IEnumerator InitializeAddressables()
        {
            var initHandle = Addressables.InitializeAsync();
            yield return initHandle;

            if (initHandle.Status != AsyncOperationStatus.Succeeded)
                Debug.LogError("[Addressables] Failed to initialize.");

            Addressables.Release(initHandle);
        }

        private IEnumerator LoadPlayerData()
        {
            bool done = false;
            PlayerData data = null;

            yield return StartCoroutine(
                BackendClient.Instance.GetPlayerData(
                    AuthService.Instance.CurrentUser.UserId,
                    result =>
                    {
                        data = result;
                        done = true;
                    },
                    error =>
                    {
                        Debug.LogError($"[GameManager] Failed to load player data: {error}");
                        done = true;
                    }
                )
            );

            LocalPlayer = data;
        }

        #endregion

        // ─────────────────────────────────────────────
        #region State Management

        public void SetState(GameState newState)
        {
            if (CurrentState == newState) return;

            CurrentState = newState;
            OnGameStateChanged?.Invoke(newState);
            Debug.Log($"[GameManager] State → {newState}");
        }

        public void JoinWorld(string worldId)
        {
            SetState(GameState.LoadingWorld);
            StartCoroutine(JoinWorldCoroutine(worldId));
        }

        private IEnumerator JoinWorldCoroutine(string worldId)
        {
            UIManager.ShowLoadingScreen(true, "Entering world...");

            // Request server address from matchmaker
            ServerInfo serverInfo = null;
            yield return StartCoroutine(
                BackendClient.Instance.GetWorldServer(
                    worldId,
                    info => serverInfo = info,
                    err => Debug.LogError($"Matchmaking error: {err}")
                )
            );

            if (serverInfo == null)
            {
                UIManager.ShowError("Could not connect to world server.");
                SetState(GameState.MainMenu);
                yield break;
            }

            // Connect via Mirror
            NetworkManager.StartClient(serverInfo.Address, serverInfo.Port);

            // Wait for connection confirmation
            float timeout = 10f;
            float elapsed = 0f;
            while (!NetworkManager.IsConnected && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!NetworkManager.IsConnected)
            {
                UIManager.ShowError("Connection timed out.");
                SetState(GameState.MainMenu);
                yield break;
            }

            UIManager.ShowLoadingScreen(false);
            SetState(GameState.InWorld);
        }

        public void LeaveWorld()
        {
            NetworkManager.Disconnect();
            SetState(GameState.MainMenu);
            WorldEngine.UnloadCurrentWorld();
        }

        public void QuitGame()
        {
            StartCoroutine(GracefulQuit());
        }

        private IEnumerator GracefulQuit()
        {
            yield return StartCoroutine(BackendClient.Instance.SavePlayerSession());
            Application.Quit();
        }

        #endregion
    }

    public enum GameState
    {
        Booting,
        Authentication,
        MainMenu,
        LoadingWorld,
        InWorld,
        Paused
    }
}
