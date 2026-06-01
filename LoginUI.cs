using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;
using BlockVerse.Core;

namespace BlockVerse.UI
{
    /// <summary>
    /// Complete authentication UI: Login, Register, Guest flows.
    /// Handles Firebase + BlockVerse registration in one screen.
    /// </summary>
    public class LoginUI : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private CanvasGroup rootGroup;
        [SerializeField] private GameObject  loginPanel;
        [SerializeField] private GameObject  registerPanel;
        [SerializeField] private GameObject  usernamePanel; // new player: pick username
        [SerializeField] private GameObject  loadingPanel;

        // ── Login Panel ──
        [Header("Login Fields")]
        [SerializeField] private TMP_InputField loginEmail;
        [SerializeField] private TMP_InputField loginPassword;
        [SerializeField] private Button         loginBtn;
        [SerializeField] private Button         guestBtn;
        [SerializeField] private Button         toRegisterBtn;
        [SerializeField] private TextMeshProUGUI loginErrorText;
        [SerializeField] private Toggle          rememberMeToggle;

        // ── Register Panel ──
        [Header("Register Fields")]
        [SerializeField] private TMP_InputField regEmail;
        [SerializeField] private TMP_InputField regPassword;
        [SerializeField] private TMP_InputField regPasswordConfirm;
        [SerializeField] private Button         registerBtn;
        [SerializeField] private Button         toLoginBtn;
        [SerializeField] private TextMeshProUGUI regErrorText;

        // ── Username Panel ──
        [Header("Username Fields")]
        [SerializeField] private TMP_InputField usernameField;
        [SerializeField] private Button         confirmUsernameBtn;
        [SerializeField] private TextMeshProUGUI usernameErrorText;
        [SerializeField] private TextMeshProUGUI usernameAvailableText;
        [SerializeField] private Image           usernameCheckIcon;

        // ── Misc ──
        [Header("Misc")]
        [SerializeField] private Animator logoAnimator;
        [SerializeField] private ParticleSystem bgParticles;
        [SerializeField] private Image          loadingSpinner;

        private string _pendingFirebaseToken;
        private bool   _checkingUsername;
        private Coroutine _usernameCheckRoutine;

        // ─────────────────────────────────────────────
        #region Init

        private void Start()
        {
            // Wire buttons
            loginBtn.onClick.AddListener(OnLogin);
            guestBtn.onClick.AddListener(OnGuestLogin);
            toRegisterBtn.onClick.AddListener(() => SwitchPanel(registerPanel));
            registerBtn.onClick.AddListener(OnRegister);
            toLoginBtn.onClick.AddListener(() => SwitchPanel(loginPanel));
            confirmUsernameBtn.onClick.AddListener(OnConfirmUsername);

            // Username live check
            usernameField.onValueChanged.AddListener(OnUsernameChanged);

            // Auto-fill saved email
            if (PlayerPrefs.GetInt("remember_me", 0) == 1)
            {
                loginEmail.text = PlayerPrefs.GetString("saved_email", "");
                rememberMeToggle.isOn = true;
            }

            // Intro animation
            rootGroup.alpha = 0;
            rootGroup.DOFade(1f, 0.8f);
            logoAnimator?.SetTrigger("Intro");
            bgParticles?.Play();

            SwitchPanel(loginPanel);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Login

        private void OnLogin()
        {
            ClearErrors();
            string email = loginEmail.text.Trim();
            string pass  = loginPassword.text;

            if (!ValidateEmail(email))  { ShowLoginError("Enter a valid email address."); return; }
            if (pass.Length < 6)        { ShowLoginError("Password must be at least 6 characters."); return; }

            if (rememberMeToggle.isOn)
            {
                PlayerPrefs.SetInt("remember_me", 1);
                PlayerPrefs.SetString("saved_email", email);
            }
            else
            {
                PlayerPrefs.SetInt("remember_me", 0);
                PlayerPrefs.DeleteKey("saved_email");
            }

            SetLoading(true);
            StartCoroutine(AuthService.Instance.LoginEmail(
                email, pass,
                () =>
                {
                    SetLoading(false);
                    StartCoroutine(PostLoginFlow());
                },
                err =>
                {
                    SetLoading(false);
                    ShowLoginError(err);
                }
            ));
        }

        private void OnGuestLogin()
        {
            SetLoading(true, "Joining as guest...");
            StartCoroutine(AuthService.Instance.LoginAsGuest(
                () => { SetLoading(false); StartCoroutine(PostLoginFlow()); },
                err => { SetLoading(false); ShowLoginError(err); }
            ));
        }

        private IEnumerator PostLoginFlow()
        {
            // Load player data
            yield return GameManager.Instance.LoadPlayerDataCoroutine();
            GameManager.Instance.SetState(GameState.MainMenu);
            gameObject.SetActive(false);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Register

        private void OnRegister()
        {
            ClearErrors();
            string email = regEmail.text.Trim();
            string pass  = regPassword.text;
            string pass2 = regPasswordConfirm.text;

            if (!ValidateEmail(email))    { ShowRegError("Enter a valid email address."); return; }
            if (pass.Length < 6)          { ShowRegError("Password must be at least 6 characters."); return; }
            if (pass != pass2)            { ShowRegError("Passwords do not match."); return; }

            SetLoading(true, "Creating account...");

            // Firebase sign-up only — username picked after
            StartCoroutine(AuthService.Instance.RegisterEmailOnly(
                email, pass,
                token =>
                {
                    SetLoading(false);
                    _pendingFirebaseToken = token;
                    SwitchPanel(usernamePanel);
                },
                err =>
                {
                    SetLoading(false);
                    ShowRegError(err);
                }
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Username Selection

        private void OnUsernameChanged(string value)
        {
            usernameAvailableText.text = "";
            usernameCheckIcon.gameObject.SetActive(false);
            confirmUsernameBtn.interactable = false;

            if (_usernameCheckRoutine != null) StopCoroutine(_usernameCheckRoutine);
            if (value.Length >= 3)
                _usernameCheckRoutine = StartCoroutine(CheckUsernameAvailability(value));
        }

        private IEnumerator CheckUsernameAvailability(string username)
        {
            yield return new WaitForSeconds(0.5f); // debounce

            // Validate format locally first
            if (!System.Text.RegularExpressions.Regex.IsMatch(username, @"^[a-zA-Z0-9_]{3,20}$"))
            {
                usernameAvailableText.text  = "Letters, numbers, underscores only (3-20 chars)";
                usernameAvailableText.color = Color.red;
                yield break;
            }

            bool available = false;
            yield return BackendClient.Instance.CheckUsername(
                username, result => available = result, _ => { });

            usernameAvailableText.text  = available ? "✓ Available!" : "✗ Already taken";
            usernameAvailableText.color = available ? Color.green   : Color.red;
            usernameCheckIcon.gameObject.SetActive(true);
            confirmUsernameBtn.interactable = available;
        }

        private void OnConfirmUsername()
        {
            string username = usernameField.text.Trim();
            if (string.IsNullOrEmpty(username)) return;

            SetLoading(true, "Setting up account...");
            StartCoroutine(BackendClient.Instance.RegisterWithUsername(
                _pendingFirebaseToken, username,
                result =>
                {
                    BackendClient.Instance.SetTokens(result.AccessToken, result.RefreshToken);
                    SetLoading(false);
                    StartCoroutine(PostLoginFlow());
                },
                err =>
                {
                    SetLoading(false);
                    usernameErrorText.text = err;
                }
            ));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Helpers

        private void SwitchPanel(GameObject target)
        {
            loginPanel.SetActive(false);
            registerPanel.SetActive(false);
            usernamePanel.SetActive(false);
            target.SetActive(true);

            var cg = target.GetComponent<CanvasGroup>() ?? target.AddComponent<CanvasGroup>();
            cg.alpha = 0;
            cg.DOFade(1f, 0.25f);
        }

        private void SetLoading(bool active, string msg = "Loading...")
        {
            loadingPanel.SetActive(active);
            loginBtn.interactable     = !active;
            registerBtn.interactable  = !active;
            guestBtn.interactable     = !active;

            if (active)
                loadingSpinner.transform
                    .DORotate(new Vector3(0, 0, -360), 1f, RotateMode.FastBeyond360)
                    .SetLoops(-1).SetEase(Ease.Linear);
            else
                DOTween.Kill(loadingSpinner.transform);
        }

        private void ShowLoginError(string msg)
        {
            loginErrorText.text = msg;
            loginErrorText.gameObject.SetActive(true);
            loginErrorText.transform.DOShakePosition(0.3f, 5f, 15);
        }

        private void ShowRegError(string msg)
        {
            regErrorText.text = msg;
            regErrorText.gameObject.SetActive(true);
        }

        private void ClearErrors()
        {
            loginErrorText.gameObject.SetActive(false);
            regErrorText.gameObject.SetActive(false);
            usernameErrorText.text = "";
        }

        private static bool ValidateEmail(string email)
            => System.Text.RegularExpressions.Regex.IsMatch(
                email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

        #endregion
    }
}
