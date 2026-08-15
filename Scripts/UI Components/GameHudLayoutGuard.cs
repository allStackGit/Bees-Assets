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
    /// scene-wide UI compatibility fixes to legacy controls and display configurations.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameHudLayoutGuard : MonoBehaviour
    {
        private const float ControlGap = 10f;
        private const float TitaniaClockGap = 5f;
        private const float DynamicButtonScanInterval = 1f;
        private const float SquadTabLeftMargin = 10f;
        private const float SquadTabTopMargin = 10f;
        private const float SquadTabGap = 8f;
        private const float BottomHudMargin = 10f;
        private static readonly Vector2 DefaultReferenceResolution = new Vector2(1366f, 768f);

        private GameMenus _menus;
        private RectTransform _clockRect;
        private RectTransform _counterRect;
        private RectTransform _speedRect;
        private RectTransform _plutoShieldRect;
        private Vector2 _normalSpeedPosition;
        private bool _clockWasVisible;
        private bool _initialized;
        private bool _actionBoxWasVisible;
        private bool _actionBoxNeedsClamp = true;
        private int _normalizedSquadTabCount = -1;
        private float _nextDynamicButtonScan;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            RefreshLiveScreenDimensions();
            ApplyAspectRatioSafeCanvasScaling(scene);
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

        private static void RefreshLiveScreenDimensions()
        {
            // ConfigData historically captured Screen.width/height once during static initialization.
            // On macOS/Retina and after resolution or window-size changes that snapshot can differ
            // from the actual client area, which makes LevelInputManager miss the right/top edge.
            ConfigData.ScreenWidth = Screen.width;
            ConfigData.ScreenHeight = Screen.height;
        }

        private static void ApplyAspectRatioSafeCanvasScaling(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (CanvasScaler scaler in root.GetComponentsInChildren<CanvasScaler>(true))
                {
                    Canvas canvas = scaler.GetComponent<Canvas>();
                    if (canvas == null || canvas.renderMode == RenderMode.WorldSpace)
                    {
                        continue;
                    }

                    // MatchWidthOrHeight with a width-only match was authored around 1366x768 and
                    // can crop fixed-position legacy controls on 16:10, ultrawide and tall displays.
                    // Expand chooses the smaller scale ratio, guaranteeing that the full reference
                    // rectangle remains available. Proper edge anchors then stay on their edges.
                    scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    if (scaler.referenceResolution.x <= 0f || scaler.referenceResolution.y <= 0f)
                    {
                        scaler.referenceResolution = DefaultReferenceResolution;
                    }
                    scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
                }
            }
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
            _clockRect = _menus.Clock != null
                ? _menus.Clock.GetComponent<RectTransform>()
                : null;
            _counterRect = _menus.Counter != null
                ? _menus.Counter.GetComponent<RectTransform>()
                : null;
            _speedRect = _menus.GameSpeedButton != null
                ? _menus.GameSpeedButton.GetComponent<RectTransform>()
                : null;
            _plutoShieldRect = _menus.PlutoShield != null
                ? _menus.PlutoShield.GetComponent<RectTransform>()
                : null;

            if (_speedRect != null)
            {
                _normalSpeedPosition = _speedRect.anchoredPosition;
            }
            _clockWasVisible = false;
            _initialized = true;
            ApplyLayout();
        }

        private void Update()
        {
            if (Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                RefreshLiveScreenDimensions();
                _actionBoxNeedsClamp = true;
            }

            NormalizeSquadTabs();
            UpdateDynamicButtonStyles();
        }

        private void NormalizeSquadTabs()
        {
            if (_menus == null || _menus.Stage == null || _menus.Stage.SquadTabs == null)
            {
                return;
            }

            int tabCount = _menus.Stage.SquadTabs.Count;
            if (tabCount == 0 || tabCount == _normalizedSquadTabCount)
            {
                return;
            }

            float x = SquadTabLeftMargin;
            for (int i = 0; i < tabCount; i++)
            {
                SquadTab tab = _menus.Stage.SquadTabs[i];
                if (tab == null || tab.Tab == null)
                {
                    continue;
                }

                RectTransform rect = tab.Tab.GetComponent<RectTransform>();
                if (rect == null)
                {
                    continue;
                }

                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(x, -SquadTabTopMargin);
                x += rect.rect.width + SquadTabGap;
            }

            _normalizedSquadTabCount = tabCount;
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

        private void LateUpdate()
        {
            if (!_initialized || _menus == null)
            {
                return;
            }

            KeepActionBoxWithinCanvas();

            if (_menus.Clock == null || _menus.GameSpeedButton == null ||
                _clockRect == null || _speedRect == null)
            {
                return;
            }

            bool clockVisible = _menus.Clock.activeInHierarchy;
            if (clockVisible != _clockWasVisible || clockVisible)
            {
                ApplyLayout();
            }
        }

        private void KeepActionBoxWithinCanvas()
        {
            GameObject actionBox = _menus.SquadActionBoxUI;
            bool visible = actionBox != null && actionBox.activeInHierarchy;
            if (!visible)
            {
                _actionBoxWasVisible = false;
                return;
            }

            if (!_actionBoxWasVisible)
            {
                _actionBoxNeedsClamp = true;
            }
            _actionBoxWasVisible = true;

            if (!_actionBoxNeedsClamp)
            {
                return;
            }

            RectTransform actionRect = actionBox.GetComponent<RectTransform>();
            Canvas canvas = actionBox.GetComponentInParent<Canvas>();
            RectTransform canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            if (actionRect == null || canvasRect == null)
            {
                return;
            }

            // The legacy level ActionBox is a zero-sized root whose visible child panel is centered
            // around that root. On some aspect ratios the root sits at the lower-left canvas edge,
            // which leaves part of the child panel below the screen. Clamp the actual descendant
            // bounds instead of assuming the root RectTransform describes what the player sees.
            Canvas.ForceUpdateCanvases();
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, actionRect);
            if (bounds.size.x < 1f || bounds.size.y < 1f)
            {
                // Its layout/scale has not been initialized yet. Try again next LateUpdate.
                return;
            }

            Rect available = canvasRect.rect;
            float minX = available.xMin + BottomHudMargin;
            float maxX = available.xMax - BottomHudMargin;
            float minY = available.yMin + BottomHudMargin;
            float maxY = available.yMax - BottomHudMargin;
            Vector2 correction = Vector2.zero;

            if (bounds.min.x < minX)
            {
                correction.x = minX - bounds.min.x;
            }
            else if (bounds.max.x > maxX)
            {
                correction.x = maxX - bounds.max.x;
            }

            if (bounds.min.y < minY)
            {
                correction.y = minY - bounds.min.y;
            }
            else if (bounds.max.y > maxY)
            {
                correction.y = maxY - bounds.max.y;
            }

            if (correction != Vector2.zero)
            {
                Vector3 worldCorrection = canvasRect.TransformVector(
                    new Vector3(correction.x, correction.y, 0f));
                actionRect.position += worldCorrection;
            }

            _actionBoxNeedsClamp = false;
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
            if (_menus == null || _menus.Clock == null || _menus.GameSpeedButton == null ||
                _clockRect == null || _speedRect == null)
            {
                return;
            }

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

                Vector2 desiredPosition = new Vector2(x, y);
                if (_speedRect.anchoredPosition != desiredPosition)
                {
                    _speedRect.anchoredPosition = desiredPosition;
                }
            }
            else if (_clockWasVisible && _speedRect.anchoredPosition != _normalSpeedPosition)
            {
                _speedRect.anchoredPosition = _normalSpeedPosition;
            }

            _clockWasVisible = clockVisible;
        }
    }
}
