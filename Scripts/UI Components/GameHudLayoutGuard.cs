using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Assets.Scripts.Scenes;
using Assets.Scripts.UIComponents;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps optional top-level HUD controls from occupying the same screen space and applies
    /// small scene-wide UI compatibility fixes to legacy controls.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameHudLayoutGuard : MonoBehaviour
    {
        private const float ControlGap = 10f;
        private const float TitaniaClockGap = 5f;
        private const float DynamicButtonScanInterval = 0.25f;

        private GameMenus _menus;
        private RectTransform _clockRect;
        private RectTransform _counterRect;
        private RectTransform _speedRect;
        private RectTransform _plutoShieldRect;
        private Vector2 _normalSpeedPosition;
        private bool _clockWasVisible;
        private bool _initialized;
        private bool _mouseScrollSuppressed;
        private bool _savedStageMouseScrolling;
        private bool _savedUserMouseScrolling;
        private Stage _mouseScrollStage;
        private float _nextDynamicButtonScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            ApplyReadableInputFieldStyle(scene);
            ApplyButtonInteractionStyle(scene);

            GameMenus menus = Object.FindObjectOfType<GameMenus>();
            if (menus == null || menus.gameObject.GetComponent<GameHudLayoutGuard>() != null)
            {
                return;
            }

            GameHudLayoutGuard guard = menus.gameObject.AddComponent<GameHudLayoutGuard>();
            guard.Initialize(menus);
        }

        private static void ApplyReadableInputFieldStyle(UnityEngine.SceneManagement.Scene scene)
        {
            // Match the normal green button face exactly: RGB 30, 207, 136 (#1ECF88).
            Color background = new Color32(30, 207, 136, 255);
            Color foreground = ConfigData.GetUIColor("supply-capacity-label");

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (TMP_InputField input in root.GetComponentsInChildren<TMP_InputField>(true))
                {
                    if (input.targetGraphic != null)
                    {
                        input.targetGraphic.color = background;
                    }

                    // Selectable state colors multiply the input background. Keep focus and hover
                    // at full brightness so the selected field cannot become darker than idle.
                    ColorBlock colors = input.colors;
                    colors.normalColor = Color.white;
                    colors.highlightedColor = Color.white;
                    colors.selectedColor = Color.white;
                    colors.pressedColor = Color.white;
                    colors.disabledColor = new Color(1f, 1f, 1f, 0.55f);
                    colors.colorMultiplier = 1f;
                    input.colors = colors;

                    if (input.textComponent != null)
                    {
                        input.textComponent.color = foreground;
                    }
                    if (input.placeholder is TMP_Text placeholder)
                    {
                        Color placeholderColor = foreground;
                        placeholderColor.a = 0.65f;
                        placeholder.color = placeholderColor;
                    }
                    input.customCaretColor = true;
                    input.caretColor = foreground;
                }
            }
        }

        private static void ApplyButtonInteractionStyle(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Button button in root.GetComponentsInChildren<Button>(true))
                {
                    ConfigureButtonStyle(button);
                }
            }
        }

        internal static void ConfigureButtonStyle(Button button)
        {
            if (button == null)
            {
                return;
            }

            // Menu buttons are authored with a four-pixel baked border. Render their imported
            // sprite border as a nine-slice so narrow/tall controls preserve the same edge weight
            // without the extra dark-green runtime Outline that previously framed every button.
            if (button.targetGraphic is Image image && image.sprite != null &&
                image.sprite.name.StartsWith("menu_button"))
            {
                image.type = Image.Type.Sliced;
            }

            // The shared red X is authored at only 16x16. Enlarge the selectable itself so hover
            // and pointer-up do not fall off the button with normal hand movement.
            if (button.gameObject.name == "Close Button")
            {
                RectTransform rect = button.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.sizeDelta = new Vector2(
                        Mathf.Max(rect.sizeDelta.x, 28f),
                        Mathf.Max(rect.sizeDelta.y, 28f));
                }
            }
        }

        private void Initialize(GameMenus menus)
        {
            _menus = menus;
            if (_menus.Clock == null || _menus.GameSpeedButton == null)
            {
                enabled = false;
                return;
            }

            _clockRect = _menus.Clock.GetComponent<RectTransform>();
            _counterRect = _menus.Counter != null
                ? _menus.Counter.GetComponent<RectTransform>()
                : null;
            _speedRect = _menus.GameSpeedButton.GetComponent<RectTransform>();
            _plutoShieldRect = _menus.PlutoShield != null
                ? _menus.PlutoShield.GetComponent<RectTransform>()
                : null;

            if (_clockRect == null || _speedRect == null)
            {
                enabled = false;
                return;
            }

            _normalSpeedPosition = _speedRect.anchoredPosition;
            _clockWasVisible = false;
            _initialized = true;
            ApplyLayout();
        }

        private void Update()
        {
            UpdateMouseScrollOwnership();
            UpdateDynamicButtonStyles();
        }

        private void UpdateDynamicButtonStyles()
        {
            if (_menus == null || Time.unscaledTime < _nextDynamicButtonScan)
            {
                return;
            }
            _nextDynamicButtonScan = Time.unscaledTime + DynamicButtonScanInterval;

            // Tooltips and some end-state UI are instantiated after sceneLoaded. Re-scan the menu
            // hierarchy at a low frequency so their Close Buttons receive the same usable hit area.
            foreach (Button button in _menus.GetComponentsInChildren<Button>(true))
            {
                ConfigureButtonStyle(button);
            }
        }

        private void UpdateMouseScrollOwnership()
        {
            Stage stage = _menus != null ? _menus.Stage : null;
            if (stage == null || ConfigData.UserProgressData == null)
            {
                RestoreMouseScrolling();
                return;
            }

            Vector3 mouse = Input.mousePosition;
            bool pointerInsideWindow = Application.isFocused &&
                                       mouse.x >= 0f && mouse.x < Screen.width &&
                                       mouse.y >= 0f && mouse.y < Screen.height;
            if (pointerInsideWindow)
            {
                RestoreMouseScrolling();
                return;
            }

            if (!_mouseScrollSuppressed || _mouseScrollStage != stage)
            {
                RestoreMouseScrolling();
                _mouseScrollStage = stage;
                _savedStageMouseScrolling = stage.UseMouseScrolling;
                _savedUserMouseScrolling = ConfigData.UserProgressData.UseMouseScrolling;
                _mouseScrollSuppressed = true;
            }

            // LevelInputManager enables edge scrolling when either of these values is true.
            // Temporarily suppress both while the pointer is outside the client rectangle so
            // negative/off-window coordinates cannot masquerade as a screen edge.
            stage.UseMouseScrolling = false;
            ConfigData.UserProgressData.UseMouseScrolling = false;
        }

        private void RestoreMouseScrolling()
        {
            if (!_mouseScrollSuppressed)
            {
                return;
            }

            if (_mouseScrollStage != null)
            {
                _mouseScrollStage.UseMouseScrolling = _savedStageMouseScrolling;
            }
            if (ConfigData.UserProgressData != null)
            {
                ConfigData.UserProgressData.UseMouseScrolling = _savedUserMouseScrolling;
            }

            _mouseScrollStage = null;
            _mouseScrollSuppressed = false;
        }

        private void OnDisable()
        {
            RestoreMouseScrolling();
        }

        private void OnDestroy()
        {
            RestoreMouseScrolling();
        }

        private void LateUpdate()
        {
            if (!_initialized || _menus == null || _menus.Clock == null || _menus.GameSpeedButton == null)
            {
                return;
            }

            bool clockVisible = _menus.Clock.activeInHierarchy;
            if (clockVisible != _clockWasVisible || clockVisible)
            {
                ApplyLayout();
            }
        }

        private static int GetCampaignMissionId()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                ConfigData.UserProgressData == null || ConfigData.Configuration == null)
            {
                return -1;
            }

            return ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
        }

        private void ApplyLayout()
        {
            bool clockVisible = _menus.Clock.activeInHierarchy;
            if (clockVisible)
            {
                // Both controls are authored in the same HUD coordinate space. The default timed
                // mission layout puts the speed button immediately to the left of the clock.
                float x = _clockRect.anchoredPosition.x -
                          ((_clockRect.rect.width + _speedRect.rect.width) * 0.5f) - ControlGap;
                float y = _clockRect.anchoredPosition.y;
                int campaignMissionId = GetCampaignMissionId();

                if (campaignMissionId == 8)
                {
                    // Titania II has open HUD space on the right beneath the clock. Right-align the
                    // speed button with the clock so it stays in that column instead of floating
                    // over the play field or borrowing Pluto IV's shield/counter layout.
                    x = _clockRect.anchoredPosition.x +
                        ((_clockRect.rect.width - _speedRect.rect.width) * 0.5f);
                    y = _clockRect.anchoredPosition.y -
                        ((_clockRect.rect.height + _speedRect.rect.height) * 0.5f) - TitaniaClockGap;
                }
                else if (campaignMissionId == 3 &&
                         _plutoShieldRect != null &&
                         _menus.PlutoShield != null &&
                         _menus.PlutoShield.activeInHierarchy)
                {
                    // Pluto IV uses the whole top row for the planetary shield and mission clock.
                    // Preserve its established evacuation-counter alignment independently of the
                    // Titania II layout above.
                    if (_counterRect != null &&
                        _menus.Counter != null &&
                        _menus.Counter.activeInHierarchy)
                    {
                        // Align the button's top edge with the evacuation counter's top edge.
                        y = _counterRect.anchoredPosition.y +
                            ((_counterRect.rect.height - _speedRect.rect.height) * 0.5f);
                    }
                    else
                    {
                        // Fallback for Pluto IV before/without the evacuation counter.
                        y = _plutoShieldRect.anchoredPosition.y -
                            ((_plutoShieldRect.rect.height + _speedRect.rect.height) * 0.5f) - ControlGap;
                    }
                }

                _speedRect.anchoredPosition = new Vector2(x, y);
            }
            else if (_clockWasVisible)
            {
                _speedRect.anchoredPosition = _normalSpeedPosition;
            }

            _clockWasVisible = clockVisible;
        }
    }
}
