using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using BlockVerse.Core;
using BlockVerse.Economy;

namespace BlockVerse
{
    /// <summary>
    /// Singleton HTTP client for all Node.js backend REST API calls.
    /// Uses UnityWebRequest with coroutine-based async flow.
    /// Handles JWT refresh automatically on 401.
    /// </summary>
    public class BackendClient : MonoBehaviour
    {
        public static BackendClient Instance { get; private set; }

        [SerializeField] private AppConfig config;

        private string _accessToken;
        private string _refreshToken;
        private bool   _isRefreshing;
        private readonly Queue<IEnumerator> _pendingRequests = new();

        private string BaseUrl => config.BackendApiUrl;

        private void Awake()
        {
            Instance = this;
        }

        public void SetTokens(string access, string refresh)
        {
            _accessToken  = access;
            _refreshToken = refresh;
        }

        // ─────────────────────────────────────────────
        #region Auth

        public IEnumerator ValidateToken(string token, Action<TokenValidationResult> onSuccess, Action<string> onError)
        {
            yield return Post<TokenValidationResult>(
                "/auth/validate-token", new { token },
                result => onSuccess?.Invoke(result),
                onError,
                useServerAuth: true
            );
        }

        public IEnumerator TryAutoLogin(Action<bool> onResult)
        {
            _accessToken  = PlayerPrefs.GetString("access_token",  "");
            _refreshToken = PlayerPrefs.GetString("refresh_token", "");

            if (string.IsNullOrEmpty(_accessToken)) { onResult(false); yield break; }

            bool success = false;
            yield return Get<PlayerData>(
                "/auth/me",
                data => { GameManager.Instance.LocalPlayer?.UpdateFrom(data); success = true; },
                err =>
                {
                    // Try refresh
                    StartCoroutine(TryRefreshToken(refreshed => { success = refreshed; }));
                }
            );

            onResult(success);
        }

        private IEnumerator TryRefreshToken(Action<bool> onResult)
        {
            if (string.IsNullOrEmpty(_refreshToken)) { onResult(false); yield break; }

            yield return Post<TokenResponse>(
                "/auth/refresh", new { refreshToken = _refreshToken },
                data =>
                {
                    _accessToken  = data.AccessToken;
                    _refreshToken = data.RefreshToken;
                    PlayerPrefs.SetString("access_token",  data.AccessToken);
                    PlayerPrefs.SetString("refresh_token", data.RefreshToken);
                    onResult(true);
                },
                err => { ClearTokens(); onResult(false); },
                skipAuth: true
            );
        }

        public void ClearTokens()
        {
            _accessToken  = "";
            _refreshToken = "";
            PlayerPrefs.DeleteKey("access_token");
            PlayerPrefs.DeleteKey("refresh_token");
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Player

        public IEnumerator GetPlayerData(string playerId,
            Action<PlayerData> onSuccess, Action<string> onError)
        {
            yield return Get<PlayerData>($"/players/{playerId}", onSuccess, onError);
        }

        public IEnumerator SavePlayerData(PlayerData data,
            Action onSuccess, Action<string> onError)
        {
            yield return Put<OkResponse>(
                $"/players/{data.PlayerId}/session", data,
                _ => onSuccess?.Invoke(), onError,
                useServerAuth: true
            );
        }

        public IEnumerator SavePlayerSession()
        {
            if (GameManager.Instance.LocalPlayer == null) yield break;

            bool done = false;
            yield return SavePlayerData(
                GameManager.Instance.LocalPlayer,
                () => done = true,
                err => { Debug.LogError($"[Backend] Save session error: {err}"); done = true; }
            );
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Worlds

        public IEnumerator GetWorldServer(string worldId,
            Action<ServerInfo> onSuccess, Action<string> onError)
        {
            yield return Post<ServerInfo>(
                $"/matchmaking/world/{worldId}", new { },
                onSuccess, onError
            );
        }

        public IEnumerator SearchWorlds(string query, int page,
            Action<WorldSearchResult> onSuccess, Action<string> onError)
        {
            yield return Get<WorldSearchResult>(
                $"/worlds?search={UnityWebRequest.EscapeURL(query)}&page={page}",
                onSuccess, onError
            );
        }

        public IEnumerator CreateWorld(string name, bool isLocked,
            Action<string> onSuccess, Action<string> onError)
        {
            yield return Post<CreateWorldResponse>(
                "/worlds", new { name, isLocked },
                result => onSuccess?.Invoke(result.WorldId),
                onError
            );
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Economy / Shop

        public IEnumerator GetShopItems(
            Action<List<ShopItemData>> onSuccess, Action<string> onError)
        {
            yield return Get<List<ShopItemData>>("/economy/shop", onSuccess, onError);
        }

        public IEnumerator PurchaseShopItem(string shopItemId,
            Action onSuccess, Action<string> onError)
        {
            yield return Post<OkResponse>(
                $"/economy/shop/{shopItemId}/buy", new { },
                _ => onSuccess?.Invoke(), onError
            );
        }

        public IEnumerator ValidateIAPReceipt(string productId, string receipt,
            Action<bool> onSuccess, Action<string> onError)
        {
            yield return Post<IAPValidationResult>(
                "/economy/iap/validate", new { productId, receipt },
                result => onSuccess?.Invoke(result.Valid),
                onError
            );
        }

        public IEnumerator CreditOfflinePlayer(string playerId, int itemId, int count,
            Action onSuccess, Action<string> onError)
        {
            yield return Post<OkResponse>(
                $"/players/{playerId}/credit", new { itemId, count },
                _ => onSuccess?.Invoke(), onError,
                useServerAuth: true
            );
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Guild

        public IEnumerator CreateGuild(string ownerId, string name, string desc,
            Action<string> onSuccess, Action<string> onError)
        {
            yield return Post<CreateGuildResponse>(
                "/guild", new { ownerId, name, description = desc },
                result => onSuccess?.Invoke(result.GuildId),
                onError
            );
        }

        public IEnumerator AddGuildMember(string guildId, string playerId,
            Action onSuccess, Action<string> onError)
        {
            yield return Post<OkResponse>(
                $"/guild/{guildId}/members", new { playerId },
                _ => onSuccess?.Invoke(), onError
            );
        }

        public IEnumerator RemoveGuildMember(string guildId, string playerId,
            Action onSuccess, Action<string> onError)
        {
            yield return Delete<OkResponse>(
                $"/guild/{guildId}/members/{playerId}",
                _ => onSuccess?.Invoke(), onError
            );
        }

        public IEnumerator UpdateGuildMemberRole(string guildId, string playerId, string role,
            Action onSuccess, Action<string> onError)
        {
            yield return Patch<OkResponse>(
                $"/guild/{guildId}/members/{playerId}", new { role },
                _ => onSuccess?.Invoke(), onError
            );
        }

        public IEnumerator DeleteGuild(string guildId,
            Action onSuccess, Action<string> onError)
        {
            yield return Delete<OkResponse>(
                $"/guild/{guildId}",
                _ => onSuccess?.Invoke(), onError
            );
        }

        #endregion

        // ─────────────────────────────────────────────
        #region AntiCheat

        public void LogAntiCheatEvent(Security.AntiCheatEvent evt)
        {
            StartCoroutine(Post<OkResponse>(
                "/players/anticheat/log",
                new { playerId = evt.PlayerId, violation = evt.Violation.ToString(), details = evt.Details },
                null, null, useServerAuth: true
            ));
        }

        public void AutoBanPlayer(string playerId, string reason, int durationSeconds)
        {
            StartCoroutine(Post<OkResponse>(
                "/players/anticheat/autoban",
                new { playerId, reason, durationSeconds },
                null, null, useServerAuth: true
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region HTTP Helpers

        private IEnumerator Get<T>(string path, Action<T> onSuccess, Action<string> onError)
        {
            yield return Request<T>(UnityWebRequest.kHttpVerbGET, path, null, onSuccess, onError);
        }

        private IEnumerator Post<T>(string path, object body,
            Action<T> onSuccess, Action<string> onError,
            bool useServerAuth = false, bool skipAuth = false)
        {
            yield return Request<T>(UnityWebRequest.kHttpVerbPOST, path, body,
                onSuccess, onError, useServerAuth, skipAuth);
        }

        private IEnumerator Put<T>(string path, object body,
            Action<T> onSuccess, Action<string> onError, bool useServerAuth = false)
        {
            yield return Request<T>(UnityWebRequest.kHttpVerbPUT, path, body,
                onSuccess, onError, useServerAuth);
        }

        private IEnumerator Patch<T>(string path, object body,
            Action<T> onSuccess, Action<string> onError)
        {
            yield return Request<T>("PATCH", path, body, onSuccess, onError);
        }

        private IEnumerator Delete<T>(string path,
            Action<T> onSuccess, Action<string> onError)
        {
            yield return Request<T>(UnityWebRequest.kHttpVerbDELETE, path, null, onSuccess, onError);
        }

        private IEnumerator Request<T>(string method, string path, object body,
            Action<T> onSuccess, Action<string> onError,
            bool useServerAuth = false, bool skipAuth = false)
        {
            string url  = BaseUrl + path;
            string json = body != null ? JsonConvert.SerializeObject(body) : null;

            using var req = new UnityWebRequest(url, method);
            req.downloadHandler = new DownloadHandlerBuffer();

            if (json != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.SetRequestHeader("Content-Type", "application/json");
            }

            if (!skipAuth)
            {
                if (useServerAuth)
                    req.SetRequestHeader("x-server-secret",
                        WorldServerConfig.ServerSecret);
                else if (!string.IsNullOrEmpty(_accessToken))
                    req.SetRequestHeader("Authorization", $"Bearer {_accessToken}");
            }

            req.timeout = 30;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.ConnectionError ||
                req.result == UnityWebRequest.Result.ProtocolError)
            {
                // Auto-refresh on 401
                if (req.responseCode == 401 && !skipAuth && !_isRefreshing)
                {
                    _isRefreshing = true;
                    bool refreshed = false;
                    yield return TryRefreshToken(ok => { refreshed = ok; _isRefreshing = false; });

                    if (refreshed)
                    {
                        // Retry with new token
                        yield return Request<T>(method, path, body, onSuccess, onError, useServerAuth);
                        yield break;
                    }

                    GameManager.Instance.SetState(GameState.Authentication);
                    yield break;
                }

                onError?.Invoke($"[{req.responseCode}] {req.error}");
                yield break;
            }

            try
            {
                var result = JsonConvert.DeserializeObject<T>(req.downloadHandler.text);
                onSuccess?.Invoke(result);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Parse error: {ex.Message}\nRaw: {req.downloadHandler.text}");
            }
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Response DTOs
    // ─────────────────────────────────────────────────────

    [Serializable] public class OkResponse      { public bool Ok; }
    [Serializable] public class TokenResponse   { public string AccessToken; public string RefreshToken; }
    [Serializable] public class CreateWorldResponse { public string WorldId; }
    [Serializable] public class CreateGuildResponse { public string GuildId; }
    [Serializable] public class IAPValidationResult { public bool Valid; public int GrantedCrystals; }

    [Serializable]
    public class WorldSearchResult
    {
        public List<WorldEntry> Worlds;
        public int Total;
        public int Page;
    }

    [Serializable]
    public class WorldEntry
    {
        public string WorldId;
        public string Name;
        public string OwnerId;
        public int    VisitCount;
        public int    LikeCount;
        public int    PlayerCount;
        public bool   IsLocked;
    }

    /// <summary>Server-side config read from environment or args.</summary>
    public static class WorldServerConfig
    {
        public static string CurrentWorldId { get; set; } = "hub";
        public static string ServerSecret   { get; set; } = "";
        public static string ServerId       { get; set; } = "";

        public static void LoadFromArgs()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "-worldId":   CurrentWorldId = args[i + 1]; break;
                    case "-secret":    ServerSecret   = args[i + 1]; break;
                    case "-serverId":  ServerId       = args[i + 1]; break;
                }
            }
        }
    }
}
