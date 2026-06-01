using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using BlockVerse.Core;
using BlockVerse.UI;

namespace BlockVerse
{
    /// <summary>
    /// Handles Firebase Authentication on the Unity client.
    /// Supports email/password, Google Sign-In (mobile), and Guest login.
    /// After Firebase auth, exchanges Firebase token for BlockVerse JWT.
    /// </summary>
    public class AuthService : MonoBehaviour
    {
        public static AuthService Instance { get; private set; }

        [SerializeField] private AppConfig config;

        public FirebaseUser CurrentUser { get; private set; }
        public bool IsAuthenticated => CurrentUser != null;

        private string FirebaseSignInUrl =>
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={config.FirebaseApiKey}";
        private string FirebaseSignUpUrl =>
            $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={config.FirebaseApiKey}";
        private string FirebaseRefreshUrl =>
            $"https://securetoken.googleapis.com/v1/token?key={config.FirebaseApiKey}";

        private void Awake()
        {
            Instance = this;
        }

        // ─────────────────────────────────────────────
        #region Auto Login

        public IEnumerator TryAutoLogin(Action<bool> onResult)
        {
            // Check saved refresh token
            string savedRefresh = PlayerPrefs.GetString("fb_refresh_token", "");
            if (string.IsNullOrEmpty(savedRefresh)) { onResult(false); yield break; }

            bool refreshed = false;
            yield return RefreshFirebaseToken(savedRefresh, user =>
            {
                CurrentUser = user;
                refreshed   = true;
            }, _ => { });

            if (!refreshed) { onResult(false); yield break; }

            // Exchange for BlockVerse JWT
            bool bvLogin = false;
            yield return ExchangeForBVToken(result =>
            {
                BackendClient.Instance.SetTokens(result.AccessToken, result.RefreshToken);
                PlayerPrefs.SetString("access_token",  result.AccessToken);
                PlayerPrefs.SetString("refresh_token", result.RefreshToken);
                bvLogin = true;
            }, _ => { });

            onResult(bvLogin);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Email / Password

        public IEnumerator LoginEmail(string email, string password,
            Action onSuccess, Action<string> onError)
        {
            var body = new { email, password, returnSecureToken = true };
            FirebaseAuthResponse fbResp = null;

            yield return FirebasePost<FirebaseAuthResponse>(
                FirebaseSignInUrl, body,
                r => fbResp = r,
                err => onError?.Invoke(FormatFirebaseError(err))
            );

            if (fbResp == null) yield break;

            CurrentUser = new FirebaseUser
            {
                UserId       = fbResp.localId,
                Email        = fbResp.email,
                IdToken      = fbResp.idToken,
                RefreshToken = fbResp.refreshToken,
            };

            PlayerPrefs.SetString("fb_refresh_token", fbResp.refreshToken);

            yield return ExchangeForBVToken(result =>
            {
                BackendClient.Instance.SetTokens(result.AccessToken, result.RefreshToken);
                SaveBVTokens(result);
                onSuccess?.Invoke();
            }, onError);
        }

        public IEnumerator RegisterEmail(string email, string password, string username,
            Action onSuccess, Action<string> onError)
        {
            var body = new { email, password, returnSecureToken = true };
            FirebaseAuthResponse fbResp = null;

            yield return FirebasePost<FirebaseAuthResponse>(
                FirebaseSignUpUrl, body,
                r => fbResp = r,
                err => onError?.Invoke(FormatFirebaseError(err))
            );

            if (fbResp == null) yield break;

            CurrentUser = new FirebaseUser
            {
                UserId       = fbResp.localId,
                Email        = fbResp.email,
                IdToken      = fbResp.idToken,
                RefreshToken = fbResp.refreshToken,
            };

            PlayerPrefs.SetString("fb_refresh_token", fbResp.refreshToken);

            // Register in BlockVerse backend
            yield return BackendClient.Instance.Post<TokenResponse>(
                "/auth/register",
                new { firebaseIdToken = fbResp.idToken, username },
                result =>
                {
                    BackendClient.Instance.SetTokens(result.AccessToken, result.RefreshToken);
                    SaveBVTokens(result);
                    onSuccess?.Invoke();
                },
                onError
            );
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Guest Login

        public IEnumerator LoginAsGuest(Action onSuccess, Action<string> onError)
        {
            // Firebase anonymous sign-in
            var url  = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={config.FirebaseApiKey}";
            var body = new { returnSecureToken = true };

            FirebaseAuthResponse fbResp = null;
            yield return FirebasePost<FirebaseAuthResponse>(
                url, body, r => fbResp = r, err => onError?.Invoke(err));

            if (fbResp == null) yield break;

            // Generate random guest username
            string guestName = $"Guest{UnityEngine.Random.Range(10000, 99999)}";

            CurrentUser = new FirebaseUser
            {
                UserId       = fbResp.localId,
                Email        = $"guest_{fbResp.localId}@blockverse.guest",
                IdToken      = fbResp.idToken,
                RefreshToken = fbResp.refreshToken,
                IsGuest      = true,
            };

            PlayerPrefs.SetString("fb_refresh_token", fbResp.refreshToken);

            yield return BackendClient.Instance.Post<TokenResponse>(
                "/auth/register",
                new { firebaseIdToken = fbResp.idToken, username = guestName },
                result =>
                {
                    BackendClient.Instance.SetTokens(result.AccessToken, result.RefreshToken);
                    SaveBVTokens(result);
                    onSuccess?.Invoke();
                },
                onError
            );
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Token Exchange & Refresh

        private IEnumerator ExchangeForBVToken(Action<TokenResponse> onSuccess, Action<string> onError)
        {
            yield return BackendClient.Instance.Post<TokenResponse>(
                "/auth/login",
                new { firebaseIdToken = CurrentUser.IdToken },
                onSuccess, onError,
                skipAuth: true
            );
        }

        private IEnumerator RefreshFirebaseToken(string refreshToken,
            Action<FirebaseUser> onSuccess, Action<string> onError)
        {
            var body = new { grant_type = "refresh_token", refresh_token = refreshToken };
            FirebaseRefreshResponse resp = null;

            yield return FirebasePost<FirebaseRefreshResponse>(
                FirebaseRefreshUrl, body, r => resp = r, onError);

            if (resp == null) yield break;

            var user = new FirebaseUser
            {
                UserId       = resp.user_id,
                IdToken      = resp.id_token,
                RefreshToken = resp.refresh_token,
            };

            PlayerPrefs.SetString("fb_refresh_token", resp.refresh_token);
            onSuccess?.Invoke(user);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Logout

        public void Logout()
        {
            CurrentUser = null;
            PlayerPrefs.DeleteKey("fb_refresh_token");
            BackendClient.Instance.ClearTokens();
            GameManager.Instance.SetState(GameState.Authentication);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Helpers

        private IEnumerator FirebasePost<T>(string url, object body,
            Action<T> onSuccess, Action<string> onError)
        {
            string json = JsonConvert.SerializeObject(body);
            using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 15
            };
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.downloadHandler.text);
                yield break;
            }

            try
            {
                var result = JsonConvert.DeserializeObject<T>(req.downloadHandler.text);
                onSuccess?.Invoke(result);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Parse error: {ex.Message}");
            }
        }

        private static string FormatFirebaseError(string rawJson)
        {
            try
            {
                var err = JsonConvert.DeserializeObject<FirebaseErrorResponse>(rawJson);
                return err?.error?.message switch
                {
                    "EMAIL_NOT_FOUND"       => "No account found with that email.",
                    "INVALID_PASSWORD"      => "Incorrect password.",
                    "USER_DISABLED"         => "This account has been disabled.",
                    "EMAIL_EXISTS"          => "An account with this email already exists.",
                    "WEAK_PASSWORD : Password should be at least 6 characters" =>
                        "Password must be at least 6 characters.",
                    _ => err?.error?.message ?? "Authentication failed."
                };
            }
            catch { return "Authentication error. Please try again."; }
        }

        private static void SaveBVTokens(TokenResponse t)
        {
            PlayerPrefs.SetString("access_token",  t.AccessToken);
            PlayerPrefs.SetString("refresh_token", t.RefreshToken);
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Data types
    // ─────────────────────────────────────────────────────

    [Serializable]
    public class FirebaseUser
    {
        public string UserId;
        public string Email;
        public string IdToken;
        public string RefreshToken;
        public bool   IsGuest;
        public string Token => IdToken;
    }

    [Serializable] class FirebaseAuthResponse
    {
        public string localId;
        public string email;
        public string idToken;
        public string refreshToken;
        public string expiresIn;
    }

    [Serializable] class FirebaseRefreshResponse
    {
        public string user_id;
        public string id_token;
        public string refresh_token;
    }

    [Serializable] class FirebaseErrorResponse
    {
        public FirebaseError error;
    }
    [Serializable] class FirebaseError { public string message; }
}
