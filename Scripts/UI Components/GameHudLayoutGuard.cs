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
        private const float HudEdgeMargin = 10f;
        private const float BottomHudMargin = HudEdgeMargin;
        private static readonly Vector2 DefaultReferenceResolution = new Vector2(1366f, 768f);

        private GameMenus _menus;
        private RectTransform _clockRect;
        private RectTransform _counterRect;
        private RectTransform _speedRect;
        private RectTransform _plutoShieldRect;
        private RectTransform _scoreboardRect;
        private RectTransform _squadTabsRoot;
        private Vector2 _normalSpeedPosition;
        private bool _clockWasVisible;
        private bool _menuInitialized;
        private int _normalizedSquadTabCount = -1;
        private int _lastSquadTabLeftPadding = -1;
        private int _lastSquadTabTopPadding = -1;
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
            _scoreboardRect = _menus.Scoreboard != null ? _menus.Scoreboard.GetComponent<RectTransform>() : null;

            if (_speedRect != null)
            {
                _normalSpeedPosition = _speedRect.anchoredPosition;
            }

            _clockWasVisible = false;
            _menuInitialized = true;
            _normalizedSquadTabCount = -1;
            _lastSquadTabLeftPadding = -1;
            _lastSquadTabTopPadding = -1;
            _squadTabsRoot = null;
            ApplyLayout();
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
                _lastSquadTabLeftPadding = -1;
                _lastSquadTabTopPadding = -1;
            }

            if (_responsiveCanvas != null &&
                (_responsiveLayoutDirty || Time.unscaledTime >= _nextResponsiveLayoutScan))
            {
                ApplyResponsiveCanvasLayout();
                _nextResponsiveLayoutScan = Time.unscaledTime + ResponsiveLayoutScanInterval;
            }

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
            if (tabCount == 0)
            {
                return;
            }

            if (_squadTabsRoot == null)
            {
                RectTransform firstTabRect = null;
                for (int i = 0; i < tabCount; i++)
                {
                    SquadTab tab = _menus.Stage.SquadTabs[i];
                    if (tab == null || tab.Tab == null)
                    {
                        continue;
                    }

                    firstTabRect = tab.Tab.GetComponent<RectTransform>();
                    if (firstTabRect != null)
                    {
                        break;
                    }
                }

                _squadTabsRoot = firstTabRect != null ? firstTabRect.parent as RectTransform : null;
            }

            RectTransform tabsRoot = _squadTabsRoot;
            if (tabsRoot == null)
            {
                return;
            }

            RectTransform canvasRect = GetRootCanvasRect(tabsRoot);
            bool rootChanged = StretchToRootCanvas(tabsRoot, canvasRect);

            HorizontalLayoutGroup layout = tabsRoot.GetComponent<HorizontalLayoutGroup>();
            if (layout == null)
            {
                layout = tabsRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
                rootChanged = true;
            }

            int leftPadding = GetSquadTabLeftPadding(
                tabsRoot,
                _scoreboardRect,
                SquadTabGap,
                HudEdgeMargin);
            int topPadding = Mathf.CeilToInt(HudEdgeMargin);

            bool geometryChanged = force || rootChanged ||
                                   tabCount != _normalizedSquadTabCount ||
                                   leftPadding != _lastSquadTabLeftPadding ||
                                   topPadding != _lastSquadTabTopPadding;
            if (!geometryChanged)
            {
                return;
            }

            // The authored Space scene reserved 200 px for the scoreboard before the squad tabs.
            // Preserve that semantic relationship using live geometry rather than a fixed inset so
            // the row stays immediately to the scoreboard's right at every aspect ratio. When the
            // scoreboard is hidden, the row falls back to a small visible top-left margin.
            layout.padding = new RectOffset(leftPadding, 0, topPadding, 0);
            layout.spacing = SquadTabGap;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            LayoutRebuilder.ForceRebuildLayoutImmediate(tabsRoot);

            _normalizedSquadTabCount = tabCount;
            _lastSquadTabLeftPadding = leftPadding;
            _lastSquadTabTopPadding = topPadding;
        }

        internal static int GetSquadTabLeftPadding(
            RectTransform tabsRoot,
            RectTransform scoreboard,
            float gap,
            float fallbackMargin)
        {
            int fallback = Mathf.CeilToInt(Mathf.Max(0f, fallbackMargin));
            if (tabsRoot == null || scoreboard == null || !scoreboard.gameObject.activeInHierarchy)
            {
                return fallback;
            }

            Bounds scoreboardBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                tabsRoot,
                scoreboard);
            float fromRootLeft = scoreboardBounds.max.x - tabsRoot.rect.xMin + gap;
            return Mathf.CeilToInt(Mathf.Max(fallbackMargin, fromRootLeft));
        }

        internal static bool StretchToRootCanvas(RectTransform rect, RectTransform canvasRect)
        {
            if (rect == null || canvasRect == null || rect.parent != canvasRect ||
                canvasRect.GetComponent<LayoutGroup>() != null)
            {
                return false;
            }

            bool alreadyFilled = rect.anchorMin == Vector2.zero &&
                                 rect.anchorMax == Vector2.one &&
                                 rect.offsetMin == Vector2.zero &&
                                 rect.offsetMax == Vector2.zero;
            if (alreadyFilled)
            {
                return false;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return true;
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
            KeepScoreboardWithinCanvas();
            ApplyClockAndSpeedLayout();
            KeepNormalSpeedButtonWithinCanvas();
            KeepMissionStatusWithinCanvas();
            NormalizeSquadTabs(false);
            KeepActionBoxWithinCanvas();
            KeepMiniMapWithinCanvas();
        }

        private void KeepScoreboardWithinCanvas()
        {
            if (_menus.Scoreboard == null || !_menus.Scoreboard.activeInHierarchy || _scoreboardRect == null)
            {
                return;
            }

            ClampRectWithinCanvas(_scoreboardRect, GetRootCanvasRect(_scoreboardRect), HudEdgeMargin);
        }

        private void KeepNormalSpeedButtonWithinCanvas()
        {
            if (_menus.GameSpeedButton == null || !_menus.GameSpeedButton.activeInHierarchy ||
                _speedRect == null || (_menus.Clock != null && _menus.Clock.activeInHierarchy))
            {
                return;
            }

            ClampRectWithinCanvas(_speedRect, GetRootCanvasRect(_speedRect), HudEdgeMargin);
        }

        private void KeepMissionStatusWithinCanvas()
        {
            if (_menus.MissionStatus == null || !_menus.MissionStatus.activeInHierarchy)
            {
                return;
            }

            RectTransform statusRect = _menus.MissionStatus.GetComponent<RectTransform>();
            RectTransform layoutRoot = GetCanvasOwnedLayoutRoot(statusRect);
            RectTransform canvasRect = GetRootCanvasRect(layoutRoot);
            ClampRectWithinCanvas(layoutRoot, canvasRect, HudEdgeMargin);
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
                    x = GetRightAlignedX(
                        _clockRect.anchoredPosition.x,
                        _clockRect.rect.width,
                        _speedRect.rect.width);
                    y = GetBelowY(
                        _clockRect.anchoredPosition.y,
                        _clockRect.rect.height,
                        _speedRect.rect.height,
                        TitaniaClockGap);
                }
                else if (campaignMissionId == 3 &&
                         _plutoShieldRect != null &&
                         _menus.PlutoShield != null &&
                         _menus.PlutoShield.activeInHierarchy)
                {
                    x = GetRightAlignedX(
                        _plutoShieldRect.anchoredPosition.x,
                        _plutoShieldRect.rect.width,
                        _speedRect.rect.width);

                    if (_counterRect != null && _menus.Counter != null && _menus.Counter.activeInHierarchy)
                    {
                        y = GetTopAlignedY(
                            _counterRect.anchoredPosition.y,
                            _counterRect.rect.height,
                            _speedRect.rect.height);
                    }
                    else
                    {
                        y = GetBelowY(
                            _plutoShieldRect.anchoredPosition.y,
                            _plutoShieldRect.rect.height,
                            _speedRect.rect.height,
                            ControlGap);
                    }
                }

                Vector2 desiredPosition = new Vector2(x, y);
                if (_speedRect.anchoredPosition != desiredPosition)
                {
                    _speedRect.anchoredPosition = desiredPosition;
                }
            }
            else if (_speedRect.anchoredPosition != _normalSpeedPosition)
            {
                _speedRect.anchoredPosition = _normalSpeedPosition;
            }

            _clockWasVisible = clockVisible;
        }

        internal static float GetRightAlignedX(float referenceCenterX, float referenceWidth, float targetWidth)
        {
            return referenceCenterX + ((referenceWidth - targetWidth) * 0.5f);
        }

        internal static float GetTopAlignedY(float referenceCenterY, float referenceHeight, float targetHeight)
        {
            return referenceCenterY + ((referenceHeight - targetHeight) * 0.5f);
        }

        internal static float GetBelowY(
            float referenceCenterY,
            float referenceHeight,
            float targetHeight,
            float gap)
        {
            return referenceCenterY - ((referenceHeight + targetHeight) * 0.5f) - gap;
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

        private static RectTransform GetCanvasOwnedLayoutRoot(RectTransform rect)
        {
            if (rect == null)
            {
                return null;
            }

            RectTransform canvasRect = GetRootCanvasRect(rect);
            if (canvasRect == null)
            {
                return rect;
            }

            RectTransform current = rect;
            while (current.parent is RectTransform parent && parent != canvasRect)
            {
                current = parent;
            }
            return current;
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

        internal static bool ClampRectWithinCanvas(
            RectTransform layoutRoot,
            RectTransform canvasRect,
            float margin)
        {
            if (layoutRoot == null || canvasRect == null)
            {
                return false;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, layoutRoot);
            if (bounds.size.x < 1f || bounds.size.y < 1f)
            {
                return false;
            }

            Rect available = canvasRect.rect;
            float safeMargin = Mathf.Max(0f, margin);
            float minX = available.xMin + safeMargin;
            float maxX = available.xMax - safeMargin;
            float minY = available.yMin + safeMargin;
            float maxY = available.yMax - safeMargin;
            Vector2 correction = Vector2.zero;

            if (bounds.size.x <= maxX - minX)
            {
                if (bounds.min.x < minX)
                {
                    correction.x = minX - bounds.min.x;
                }
                else if (bounds.max.x > maxX)
                {
                    correction.x = maxX - bounds.max.x;
                }
            }

            if (bounds.size.y <= maxY - minY)
            {
                if (bounds.min.y < minY)
                {
                    correction.y = minY - bounds.min.y;
                }
                else if (bounds.max.y > maxY)
                {
                    correction.y = maxY - bounds.max.y;
                }
            }

            if (correction == Vector2.zero)
            {
                return false;
            }

            Vector3 worldCorrection = canvasRect.TransformVector(new Vector3(correction.x, correction.y, 0f));
            layoutRoot.position += worldCorrection;
            return true;
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
