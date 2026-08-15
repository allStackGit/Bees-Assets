using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Assets.Scripts.Scenes;
using Assets.Scripts.UIComponents;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Applies resolution/aspect-ratio compatibility to every screen-space canvas and keeps the
    /// gameplay HUD controls that have legacy fixed-position authoring attached to their intended
    /// screen edges. Generic screen-wrapper conversion is owned by ResponsiveScreenLayoutGuard;
    /// this component only applies semantic gameplay placement where the authored intent is known.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameHudLayoutGuard : MonoBehaviour
    {
        private const float ControlGap = 10f;
        private const float TitaniaClockGap = 5f;
        private const float DynamicButtonScanInterval = 1f;
        private const float ResponsiveLayoutScanInterval = 0.25f;
        private const float SquadTabGap = 8f;
        private const float SquadTabLeftMargin = 0f;
        private const float SquadTabTopMargin = 0f;
        private const float BottomHudMargin = 0f;
        private const float PlutoSpeedRightInset = 290f;
        private static readonly Vector2 DefaultReferenceResolution = new Vector2(1366f, 768f);

        private GameMenus _menus;
        private RectTransform _clockRect;
        private RectTransform _counterRect;
        private RectTransform _speedRect;
        private RectTransform _plutoShieldRect;
        private Vector2 _normalSpeedPosition;
        private bool _clockWasVisible;
        private bool _menuInitialized;
        private int _normalizedSquadTabCount = -1;
        private float _nextDynamicButtonScan;

        // A GameHudLayoutGuard is also attached to every root screen-space Canvas at runtime. These
        // fields are populated only on those instances; gameplay-specific fields above may remain null.
        private Canvas _responsiveCanvas;
        private CanvasScaler _responsiveScaler;
        private RectTransform _responsiveCanvasRect;
        private bool _responsiveLayoutDirty;
        private float _nextResponsiveLayoutScan;
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
            InstallResponsiveCanvasGuards(scene);
            ApplyReadableInputFieldStyle(scene);
            ApplyButtonInteractionStyle(scene);

            GameMenus menus = Object.FindObjectOfType<GameMenus>();
            if (menus == null)
            {
                return;
            }

            // The GameMenus object can itself be a Canvas root, in which case the responsive pass
            // has already added this component. Always initialize the gameplay side as well.
            GameHudLayoutGuard guard = menus.gameObject.GetComponent<GameHudLayoutGuard>();
            if (guard == null)
            {
                guard = menus.gameObject.AddComponent<GameHudLayoutGuard>();
            }
            guard.Initialize(menus);
        }

        private static void InstallResponsiveCanvasGuards(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas canvas = canvases[i];
                    if (canvas == null || canvas.renderMode == RenderMode.WorldSpace || !canvas.isRootCanvas)
                    {
                        continue;
                    }

                    CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                    if (scaler == null)
                    {
                        scaler = canvas.gameObject.AddComponent<CanvasScaler>();
                    }
                    ConfigureCanvasScaler(scaler);

                    GameHudLayoutGuard guard = canvas.gameObject.GetComponent<GameHudLayoutGuard>();
                    if (guard == null)
                    {
                        guard = canvas.gameObject.AddComponent<GameHudLayoutGuard>();
                    }
                    guard.InitializeCanvas(canvas, scaler);
                }
            }
        }

        private static void ConfigureCanvasScaler(CanvasScaler scaler)
        {
            if (scaler == null)
            {
                return;
            }

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            if (scaler.referenceResolution.x <= 0f || scaler.referenceResolution.y <= 0f)
            {
                scaler.referenceResolution = DefaultReferenceResolution;
            }
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        }

        private static void RefreshLiveScreenDimensions()
        {
            ConfigData.ScreenWidth = Screen.width;
            ConfigData.ScreenHeight = Screen.height;
        }

        private static void ApplyReadableInputFieldStyle(UnityEngine.SceneManagement.Scene scene)
        {
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

            if (button.targetGraphic is Image image && image.sprite != null &&
                image.sprite.name.StartsWith("menu_button"))
            {
                image.type = Image.Type.Sliced;
            }

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

        private void InitializeCanvas(Canvas canvas, CanvasScaler scaler)
        {
            _responsiveCanvas = canvas;
            _responsiveScaler = scaler;
            _responsiveCanvasRect = canvas != null ? canvas.transform as RectTransform : null;
            _responsiveLayoutDirty = true;
            _lastScreenWidth = -1;
            _lastScreenHeight = -1;
            ApplyResponsiveCanvasLayout();
        }

        private void Initialize(GameMenus menus)
        {
            _menus = menus;
            _clockRect = _menus.Clock != null ? _menus.Clock.GetComponent<RectTransform>() : null;
            _counterRect = _menus.Counter != null ? _menus.Counter.GetComponent<RectTransform>() : null;
            _speedRect = _menus.GameSpeedButton != null ? _menus.GameSpeedButton.GetComponent<RectTransform>() : null;
            _plutoShieldRect = _menus.PlutoShield != null ? _menus.PlutoShield.GetComponent<RectTransform>() : null;

            if (_speedRect != null)
            {
                _normalSpeedPosition = _speedRect.anchoredPosition;
            }

            _clockWasVisible = false;
            _menuInitialized = true;
            _normalizedSquadTabCount = -1;
            ApplyLayout();
            NormalizeSquadTabs(true);
        }

        private void Update()
        {
            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
            if (displayChanged)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                RefreshLiveScreenDimensions();
                _responsiveLayoutDirty = true;
                _normalizedSquadTabCount = -1;
            }

            if (_responsiveCanvas != null &&
                (_responsiveLayoutDirty || Time.unscaledTime >= _nextResponsiveLayoutScan))
            {
                ApplyResponsiveCanvasLayout();
                _nextResponsiveLayoutScan = Time.unscaledTime + ResponsiveLayoutScanInterval;
            }

            NormalizeSquadTabs(false);
            UpdateDynamicButtonStyles();
        }

        private void ApplyResponsiveCanvasLayout()
        {
            if (_responsiveCanvas == null || _responsiveCanvasRect == null ||
                _responsiveCanvas.renderMode == RenderMode.WorldSpace)
            {
                return;
            }

            // Do not translate arbitrary UI islands here. Their authored parent/anchor relationships
            // are significant (Squad Maker level text above START/TEST is one example). Generic
            // resolution repair is limited to screen-sized wrappers in ResponsiveScreenLayoutGuard.
            ConfigureCanvasScaler(_responsiveScaler);
            _responsiveLayoutDirty = false;
        }

        private void NormalizeSquadTabs(bool force)
        {
            if (_menus == null || _menus.Stage == null || _menus.Stage.SquadTabs == null)
            {
                return;
            }

            int tabCount = _menus.Stage.SquadTabs.Count;
            if (tabCount == 0 || (!force && tabCount == _normalizedSquadTabCount))
            {
                return;
            }

            RectTransform canvasRect = GetRootCanvasRect(_menus.Stage.SquadTabs, tabCount);
            if (canvasRect == null)
            {
                return;
            }

            Rect fullRect = canvasRect.rect;
            float x = fullRect.xMin + SquadTabLeftMargin;
            float y = fullRect.yMax - SquadTabTopMargin;

            for (int i = 0; i < tabCount; i++)
            {
                SquadTab tab = _menus.Stage.SquadTabs[i];
                if (tab == null || tab.Tab == null)
                {
                    continue;
                }

                RectTransform tabRect = tab.Tab.GetComponent<RectTransform>();
                if (tabRect == null)
                {
                    continue;
                }

                tabRect.anchorMin = new Vector2(0f, 1f);
                tabRect.anchorMax = new Vector2(0f, 1f);
                tabRect.pivot = new Vector2(0f, 1f);

                Vector3 worldPoint = canvasRect.TransformPoint(new Vector3(x, y, 0f));
                tabRect.position = worldPoint;
                x += tabRect.rect.width + SquadTabGap;
            }

            _normalizedSquadTabCount = tabCount;
        }

        private static RectTransform GetRootCanvasRect(System.Collections.Generic.List<SquadTab> tabs, int tabCount)
        {
            for (int i = 0; i < tabCount; i++)
            {
                SquadTab tab = tabs[i];
                if (tab == null || tab.Tab == null)
                {
                    continue;
                }

                Canvas canvas = tab.Tab.GetComponentInParent<Canvas>();
                Canvas rootCanvas = canvas != null ? canvas.rootCanvas : null;
                if (rootCanvas != null)
                {
                    return rootCanvas.transform as RectTransform;
                }
            }

            return null;
        }

        private void UpdateDynamicButtonStyles()
        {
            if (_menus == null || Time.unscaledTime < _nextDynamicButtonScan)
            {
                return;
            }

            _nextDynamicButtonScan = Time.unscaledTime + DynamicButtonScanInterval;
            foreach (Button button in _menus.GetComponentsInChildren<Button>(true))
            {
                ConfigureButtonStyle(button);
            }
        }

        private void LateUpdate()
        {
            if (!_menuInitialized || _menus == null)
            {
                return;
            }

            ApplyLayout();
        }

        private void ApplyLayout()
        {
            ApplyClockAndSpeedLayout();
            KeepActionBoxWithinCanvas();
            KeepMiniMapWithinCanvas();
        }

        private void ApplyClockAndSpeedLayout()
        {
            if (_menus.Clock == null || _menus.GameSpeedButton == null ||
                _clockRect == null || _speedRect == null)
            {
                return;
            }

            bool clockVisible = _menus.Clock.activeInHierarchy;
            if (clockVisible)
            {
                float x = _clockRect.anchoredPosition.x -
                          ((_clockRect.rect.width + _speedRect.rect.width) * 0.5f) - ControlGap;
                float y = _clockRect.anchoredPosition.y;
                int campaignMissionId = GetCampaignMissionId();

                if (campaignMissionId == 8)
                {
                    // Titania II intentionally keeps the speed control beneath the clock rather
                    // than floating to its left/right.
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
                    // Pluto IV's mission code deliberately moves Game Speed away from the
                    // planetary-shield rectangle. Preserve that horizontal contract instead of
                    // deriving x from the clock and overlapping the shield at end-of-level states.
                    x = -PlutoSpeedRightInset;

                    if (_counterRect != null && _menus.Counter != null && _menus.Counter.activeInHierarchy)
                    {
                        y = _counterRect.anchoredPosition.y +
                            ((_counterRect.rect.height - _speedRect.rect.height) * 0.5f);
                    }
                    else
                    {
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

        private void KeepActionBoxWithinCanvas()
        {
            if (_menus.SquadActionBoxUI == null || !_menus.SquadActionBoxUI.activeInHierarchy)
            {
                return;
            }

            RectTransform actionRect = _menus.SquadActionBoxUI.GetComponent<RectTransform>();
            RectTransform canvasRect = GetRootCanvasRect(actionRect);
            PinLayoutRootToCorner(actionRect, canvasRect, false, false, BottomHudMargin);
        }

        private void KeepMiniMapWithinCanvas()
        {
            GameObject output = _menus.MiniMapOutput;
            GameObject cover = _menus.MiniMapCover;
            if ((output == null || !output.activeInHierarchy) &&
                (cover == null || !cover.activeInHierarchy))
            {
                return;
            }

            RectTransform outputRect = output != null ? output.GetComponent<RectTransform>() : null;
            RectTransform coverRect = cover != null ? cover.GetComponent<RectTransform>() : null;
            RectTransform layoutRoot = GetSharedLayoutRoot(outputRect, coverRect);
            if (layoutRoot == null)
            {
                layoutRoot = coverRect != null ? coverRect : outputRect;
            }

            RectTransform canvasRect = GetRootCanvasRect(layoutRoot);
            PinLayoutRootToCorner(layoutRoot, canvasRect, true, false, BottomHudMargin);
        }

        private static RectTransform GetSharedLayoutRoot(RectTransform first, RectTransform second)
        {
            if (first != null && second != null && first.parent == second.parent && first.parent is RectTransform parent)
            {
                return parent;
            }

            return null;
        }

        private static RectTransform GetRootCanvasRect(RectTransform rect)
        {
            if (rect == null)
            {
                return null;
            }

            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Canvas rootCanvas = canvas != null ? canvas.rootCanvas : null;
            return rootCanvas != null ? rootCanvas.transform as RectTransform : null;
        }

        private static void PinLayoutRootToCorner(
            RectTransform layoutRoot,
            RectTransform canvasRect,
            bool pinRight,
            bool pinTop,
            float margin)
        {
            if (layoutRoot == null || canvasRect == null)
            {
                return;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, layoutRoot);
            if (bounds.size.x < 1f || bounds.size.y < 1f)
            {
                return;
            }

            Rect available = canvasRect.rect;
            float desiredX = pinRight
                ? available.xMax - margin - bounds.max.x
                : available.xMin + margin - bounds.min.x;
            float desiredY = pinTop
                ? available.yMax - margin - bounds.max.y
                : available.yMin + margin - bounds.min.y;

            Vector3 correction = canvasRect.TransformVector(new Vector3(desiredX, desiredY, 0f));
            layoutRoot.position += correction;
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
    }
}
