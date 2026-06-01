using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace BlockVerse.UI
{
    /// <summary>
    /// Full settings panel: Graphics, Audio, Controls, Account.
    /// Settings persist via PlayerPrefs.
    /// </summary>
    public class SettingsUI : MonoBehaviour
    {
        [Header("Tabs")]
        [SerializeField] private Button graphicsTab;
        [SerializeField] private Button audioTab;
        [SerializeField] private Button controlsTab;
        [SerializeField] private Button accountTab;

        [Header("Graphics")]
        [SerializeField] private TMP_Dropdown   qualityDropdown;
        [SerializeField] private TMP_Dropdown   resolutionDropdown;
        [SerializeField] private Toggle         fullscreenToggle;
        [SerializeField] private Toggle         vSyncToggle;
        [SerializeField] private Slider         fpsLimitSlider;
        [SerializeField] private TextMeshProUGUI fpsLimitLabel;
        [SerializeField] private Toggle         showFpsToggle;
        [SerializeField] private Slider         renderScaleSlider;
        [SerializeField] private TextMeshProUGUI renderScaleLabel;
        [SerializeField] private Toggle         particlesToggle;
        [SerializeField] private Toggle         shadowsToggle;

        [Header("Audio")]
        [SerializeField] private Slider masterSlider;
        [SerializeField] private Slider sfxSlider;
        [SerializeField] private Slider musicSlider;
        [SerializeField] private TextMeshProUGUI masterLabel;
        [SerializeField] private TextMeshProUGUI sfxLabel;
        [SerializeField] private TextMeshProUGUI musicLabel;

        [Header("Controls")]
        [SerializeField] private Slider         mouseSensSlider;
        [SerializeField] private TextMeshProUGUI mouseSensLabel;
        [SerializeField] private Toggle         invertYToggle;
        [SerializeField] private Toggle         joystickToggle;
        [SerializeField] private Slider         joystickSizeSlider;
        [SerializeField] private KeybindRow[]   keybindRows;

        [Header("Account")]
        [SerializeField] private TextMeshProUGUI usernameDisplay;
        [SerializeField] private TextMeshProUGUI emailDisplay;
        [SerializeField] private Button         changePasswordBtn;
        [SerializeField] private Button         logoutBtn;
        [SerializeField] private Button         deleteAccountBtn;

        [Header("Panels")]
        [SerializeField] private GameObject graphicsPanel;
        [SerializeField] private GameObject audioPanel;
        [SerializeField] private GameObject controlsPanel;
        [SerializeField] private GameObject accountPanel;

        [Header("Misc")]
        [SerializeField] private Button applyBtn;
        [SerializeField] private Button resetBtn;
        [SerializeField] private Button closeBtn;

        private void Start()
        {
            graphicsTab.onClick.AddListener(() => SwitchTab(graphicsPanel));
            audioTab.onClick.AddListener(() => SwitchTab(audioPanel));
            controlsTab.onClick.AddListener(() => SwitchTab(controlsPanel));
            accountTab.onClick.AddListener(() => SwitchTab(accountPanel));

            applyBtn.onClick.AddListener(ApplySettings);
            resetBtn.onClick.AddListener(ResetToDefaults);
            closeBtn.onClick.AddListener(() => UIManager.Instance.CloseActivePanel());

            logoutBtn.onClick.AddListener(OnLogout);

            LoadSettings();
            WireSliders();
            SwitchTab(graphicsPanel);
        }

        private void OnEnable() => LoadSettings();

        // ─────────────────────────────────────────────
        #region Tabs

        private void SwitchTab(GameObject target)
        {
            graphicsPanel.SetActive(false);
            audioPanel.SetActive(false);
            controlsPanel.SetActive(false);
            accountPanel.SetActive(false);

            target.SetActive(true);
            var cg = target.GetComponent<CanvasGroup>() ?? target.AddComponent<CanvasGroup>();
            cg.alpha = 0;
            cg.DOFade(1f, 0.2f);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Load / Save

        private void LoadSettings()
        {
            // Graphics
            qualityDropdown.value    = PlayerPrefs.GetInt("gfx_quality",      2);
            fullscreenToggle.isOn    = PlayerPrefs.GetInt("gfx_fullscreen",   1) == 1;
            vSyncToggle.isOn         = PlayerPrefs.GetInt("gfx_vsync",        0) == 1;
            fpsLimitSlider.value     = PlayerPrefs.GetFloat("gfx_fps",       60);
            renderScaleSlider.value  = PlayerPrefs.GetFloat("gfx_scale",     1f);
            particlesToggle.isOn     = PlayerPrefs.GetInt("gfx_particles",    1) == 1;
            shadowsToggle.isOn       = PlayerPrefs.GetInt("gfx_shadows",      1) == 1;
            showFpsToggle.isOn       = PlayerPrefs.GetInt("gfx_show_fps",     0) == 1;

            // Audio (loaded from AudioManager)
            var (master, sfx, music) = AudioManager.Instance.GetVolumes();
            masterSlider.value = master;
            sfxSlider.value    = sfx;
            musicSlider.value  = music;

            // Controls
            mouseSensSlider.value    = PlayerPrefs.GetFloat("ctrl_mouse_sens", 1f);
            invertYToggle.isOn       = PlayerPrefs.GetInt("ctrl_invert_y",    0) == 1;
            joystickToggle.isOn      = PlayerPrefs.GetInt("ctrl_joystick",    1) == 1;

            // Account
            var player = GameManager.Instance.LocalPlayer;
            if (player != null)
            {
                usernameDisplay.text = player.Username;
                emailDisplay.text    = AuthService.Instance.CurrentUser?.Email ?? "";
            }

            UpdateLabels();
        }

        private void WireSliders()
        {
            fpsLimitSlider.onValueChanged.AddListener(v =>
            {
                fpsLimitLabel.text     = v >= 120 ? "Unlimited" : $"{(int)v} FPS";
                Application.targetFrameRate = v >= 120 ? -1 : (int)v;
            });

            renderScaleSlider.onValueChanged.AddListener(v =>
                renderScaleLabel.text = $"{v:F1}x");

            masterSlider.onValueChanged.AddListener(v =>
            {
                AudioManager.Instance.SetMasterVolume(v);
                masterLabel.text = $"{(int)(v * 100)}%";
            });

            sfxSlider.onValueChanged.AddListener(v =>
            {
                AudioManager.Instance.SetSfxVolume(v);
                sfxLabel.text = $"{(int)(v * 100)}%";
            });

            musicSlider.onValueChanged.AddListener(v =>
            {
                AudioManager.Instance.SetMusicVolume(v);
                musicLabel.text = $"{(int)(v * 100)}%";
            });

            mouseSensSlider.onValueChanged.AddListener(v =>
                mouseSensLabel.text = $"{v:F1}x");
        }

        private void ApplySettings()
        {
            // Graphics
            QualitySettings.SetQualityLevel(qualityDropdown.value, true);
            Screen.fullScreen = fullscreenToggle.isOn;
            QualitySettings.vSyncCount = vSyncToggle.isOn ? 1 : 0;
            Application.targetFrameRate = fpsLimitSlider.value >= 120 ? -1 : (int)fpsLimitSlider.value;
            // renderScale applied to URP camera in production

            // Persist
            PlayerPrefs.SetInt("gfx_quality",    qualityDropdown.value);
            PlayerPrefs.SetInt("gfx_fullscreen", fullscreenToggle.isOn ? 1 : 0);
            PlayerPrefs.SetInt("gfx_vsync",      vSyncToggle.isOn      ? 1 : 0);
            PlayerPrefs.SetFloat("gfx_fps",      fpsLimitSlider.value);
            PlayerPrefs.SetFloat("gfx_scale",    renderScaleSlider.value);
            PlayerPrefs.SetInt("gfx_particles",  particlesToggle.isOn  ? 1 : 0);
            PlayerPrefs.SetInt("gfx_shadows",    shadowsToggle.isOn    ? 1 : 0);
            PlayerPrefs.SetInt("gfx_show_fps",   showFpsToggle.isOn    ? 1 : 0);
            PlayerPrefs.SetFloat("ctrl_mouse_sens", mouseSensSlider.value);
            PlayerPrefs.SetInt("ctrl_invert_y",  invertYToggle.isOn    ? 1 : 0);
            PlayerPrefs.SetInt("ctrl_joystick",  joystickToggle.isOn   ? 1 : 0);
            PlayerPrefs.Save();

            UIManager.Instance.ShowNotification("Settings saved!", Color.green);
        }

        private void ResetToDefaults()
        {
            ConfirmDialog.Show("Reset Settings",
                "Reset all settings to defaults?", "Reset",
                () =>
                {
                    PlayerPrefs.DeleteKey("gfx_quality");
                    PlayerPrefs.DeleteKey("gfx_fullscreen");
                    PlayerPrefs.DeleteKey("gfx_vsync");
                    PlayerPrefs.DeleteKey("gfx_fps");
                    PlayerPrefs.DeleteKey("gfx_scale");
                    LoadSettings();
                    ApplySettings();
                }
            );
        }

        private void UpdateLabels()
        {
            fpsLimitLabel.text     = fpsLimitSlider.value >= 120 ? "Unlimited" : $"{(int)fpsLimitSlider.value} FPS";
            renderScaleLabel.text  = $"{renderScaleSlider.value:F1}x";
            masterLabel.text       = $"{(int)(masterSlider.value * 100)}%";
            sfxLabel.text          = $"{(int)(sfxSlider.value    * 100)}%";
            musicLabel.text        = $"{(int)(musicSlider.value   * 100)}%";
            mouseSensLabel.text    = $"{mouseSensSlider.value:F1}x";
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Account Actions

        private void OnLogout()
        {
            ConfirmDialog.Show("Log Out", "Are you sure you want to log out?", "Log Out",
                () =>
                {
                    if (GameManager.Instance.CurrentState == GameState.InWorld)
                        GameManager.Instance.LeaveWorld();
                    AuthService.Instance.Logout();
                }
            );
        }

        #endregion
    }

    [System.Serializable]
    public class KeybindRow
    {
        public string ActionName;
        public Button rebindButton;
        public TextMeshProUGUI keyLabel;
    }
}
