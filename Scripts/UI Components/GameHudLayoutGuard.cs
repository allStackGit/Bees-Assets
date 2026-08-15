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
    /// screen edges. The responsive canvas portion intentionally does not depend on GameMenus so
    /// Main Menu, Squad Maker, Level Intro and other scenes receive the same treatment.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameHudLayoutGuard : MonoBehaviour
    {
        private const float ControlGap = 10f;
        private const float TitaniaClockGap = 5f;
        private const float DynamicButtonScanInterval = 1f;
        private const float ResponsiveLayoutScanInterval = 0.25f;
        private const float ResponsiveSafeMargin = 8f;
        private const float SquadTabLeftMargin = 10f;
        private const float SquadTabTopMargin = 10f;
        private const float SquadTabGap = 8f;
        private const float BottomHudMargin = 10f;
        private const int ResponsiveLayoutMaxDepth = 12;
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
        private Rect _lastSafeArea = new Rect(-1f, -1f, -1f, -1f);

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
            // has already added this component. Always initialize the gameplay side as well instead
            // of treating an existing guard as evidence that initialization is complete.
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

            // Expand uses the smaller width/height scale ratio. Unlike width-only matching, it
            // guarantees that the complete authored reference rectangle remains representable on
            // 16:10, 3:2, 4:3 and ultrawide displays rather than sacrificing one axis.
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            if (scaler.referenceResolution.x <= 0f || scaler.referenceResolution.y <= 0f)
            {
                scaler.referenceResolution = DefaultReferenceResolution;
            }
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        }

        private static void RefreshLiveScreenDimensions()
        {
            // ConfigData historically captured Screen.width/height once during static initialization.
            // On macOS/Retina and after resolution or window-size changes that snapshot can differ
            // from the actual client area, which also makes edge scrolling miss the right/top edge.
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
            _lastSafeArea = new Rect(-1f, -1f, -1f, -1f);
            ApplyResponsiveCanvasLayout();
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
            _menuInitialized = true;
            _normalizedSquadTabCount = -1;
            ApplyLayout();
            NormalizeSquadTabs(true);
        }

        private void Update()
        {
            bool displayChanged = Screen.width != _lastScreenWidth ||
                                  Screen.height != _lastScreenHeight ||
                                  !RectApproximatelyEquals(Screen.safeArea, _lastSafeArea);
            if (displayChanged)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                _lastSafeArea = Screen.safeArea;
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

        private static bool RectApproximatelyEquals(Rect a, Rect b)
        {
            return Mathf.Approximately(a.x, b.x) &&
                   Mathf.Approximately(a.y, b.y) &&
                   Mathf.Approximately(a.width, b.width) &&
                   Mathf.Approximately(a.height, b.height);
        }

        private void ApplyResponsiveCanvasLayout()
        {
            if (_responsiveCanvas == null || _responsiveCanvasRect == null ||
                _responsiveCanvas.renderMode == RenderMode.WorldSpace)
            {
                return;
            }

            ConfigureCanvasScaler(_responsiveScaler);
            Canvas.ForceUpdateCanvases();

            Rect safeRect = GetSafeCanvasRect(_responsiveCanvasRect, ResponsiveSafeMargin);
            ClampLayoutChildren(_responsiveCanvasRect, safeRect, 0);
            _responsiveLayoutDirty = false;
        }

        /// <summary>
        /// Legacy scenes often put fixed-position UI islands below one or more full-screen/stretched
        /// containers. Walk through those containers and clamp each fixed island as a unit, using
        /// the visible descendant bounds so zero-sized legacy roots are handled correctly.
        /// </summary>
        private void ClampLayoutChildren(RectTransform parent, Rect safeRect, int depth)
        {
            if (parent == null || depth >= ResponsiveLayoutMaxDepth)
            {
                return;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Canvas childCanvas = child.GetComponent<Canvas>();
                if (childCanvas != null && childCanvas != _responsiveCanvas)
                {
                    // A nested/root canvas owns its own coordinate system and guard.
                    continue;
                }

                if (IsFullScreenContainer(child))
                {
                    ClampLayoutChildren(child, safeRect, depth + 1);
                    continue;
                }

                ClampVisibleHierarchyToRect(child, _responsiveCanvasRect, safeRect);
            }
        }

        private static bool IsFullScreenContainer(RectTransform rect)
        {
            Vector2 span = rect.anchorMax - rect.anchorMin;
            return span.x >= 0.95f && span.y >= 0.95f;
        }

        private static void ClampVisibleHierarchyToRect(
            RectTransform layoutRoot,
            RectTransform canvasRect,
            Rect available)
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

            Vector2 correction = GetBoundsCorrection(bounds, available);
            if (correction == Vector2.zero)
            {
                return;
            }

            Vector3 worldCorrection = canvasRect.TransformVector(new Vector3(correction.x, correction.y, 0f));
            layoutRoot.position += worldCorrection;
        }

        private static Vector2 GetBoundsCorrection(Bounds bounds, Rect available)
        {
            Vector2 correction = Vector2.zero;

            // If an island is larger than the available area it cannot be translated fully inside;
            // leave that dimension alone rather than oscillating between opposite edges.
            if (bounds.size.x <= available.width)
            {
                if (bounds.min.x < available.xMin)
                {
                    correction.x = available.xMin - bounds.min.x;
                }
                else if (bounds.max.x > available.xMax)
                {
                    correction.x = available.xMax - bounds.max.x;
                }
            }

            if (bounds.size.y <= available.height)
            {
                if (bounds.min.y < available.yMin)
                {
                    correction.y = available.yMin - bounds.min.y;
                }
                else if (bounds.max.y > available.yMax)
                {
                    correction.y = available.yMax - bounds.max.y;
                }
            }

            return correction;
        }

        private static Rect GetSafeCanvasRect(RectTransform canvasRect, float margin)
        {
            Rect full = canvasRect.rect;
            if (Screen.width <= 0 || Screen.height <= 0)
            {
                return InsetRect(full, margin);
            }

            Rect safe = Screen.safeArea;
            float xMin = Mathf.Lerp(full.xMin, full.xMax, Mathf.Clamp01(safe.xMin / Screen.width));
            float xMax = Mathf.Lerp(full.xMin, full.xMax, Mathf.Clamp01(safe.xMax / Screen.width));
            float yMin = Mathf.Lerp(full.yMin, full.yMax, Mathf.Clamp01(safe.yMin / Screen.height));
            float yMax = Mathf.Lerp(full.yMin, full.yMax, Mathf.Clamp01(safe.yMax / Screen.height));
            return InsetRect(Rect.MinMaxRect(xMin, yMin, xMax, yMax), margin);
        }

        private static Rect InsetRect(Rect rect, float margin)
        {
            float horizontalMargin = Mathf.Min(margin, rect.width * 0.25f);
            float verticalMargin = Mathf.Min(margin, rect.height * 0.25f);
            return Rect.MinMaxRect(
                rect.xMin + horizontalMargin,
                rect.yMin + verticalMargin,
                rect.xMax - horizontalMargin,
                rect.yMax - verticalMargin);
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

            Canvas rootCanvas = null;
            RectTransform canvasRect = null;
            for (int i = 0; i < tabCount && rootCanvas == null; i++)
            {
                SquadTab tab = _menus.Stage.SquadTabs[i];
                if (tab == null || tab.Tab == null)
                {
                    continue;
                }
                Canvas canvas = tab.Tab.GetComponentInParent<Canvas>();
                rootCanvas = canvas != null ? canvas.rootCanvas : null;
                canvasRect = rootCanvas != null ? rootCanvas.transform as RectTransform : null;
            }

            if (canvasRect == null)
            {
                return;
            }

            // Scene instances historically override the Squad # prefab to bottom-left anchors and
            // all tabs live below a separate "Squad Tabs" container. Setting anchoredPosition on
            // the tab therefore does not mean screen top-left. Place each tab in root-canvas space
            // so the result is independent of its parent anchors and the display aspect ratio.
            Rect safeRect = GetSafeCanvasRect(canvasRect, 0f);
            float x = safeRect.xMin + SquadTabLeftMargin;
            float y = safeRect.yMax - SquadTabTopMargin;

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
                rect.position = canvasRect.TransformPoint(new Vector3(x, y, 0f));

                Bounds tabBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, rect);
                float width = tabBounds.size.x > 0f ? tabBounds.size.x : Mathf.Max(1f, rect.rect.width);
                x += width + SquadTabGap;
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
            if (actionBox == null || !actionBox.activeInHierarchy)
            {
                return;
            }

            RectTransform actionRect = actionBox.GetComponent<RectTransform>();
            Canvas nearestCanvas = actionBox.GetComponentInParent<Canvas>();
            Canvas rootCanvas = nearestCanvas != null ? nearestCanvas.rootCanvas : null;
            RectTransform canvasRect = rootCanvas != null ? rootCanvas.transform as RectTransform : null;
            if (actionRect == null || canvasRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, actionRect);
            if (bounds.size.x < 1f || bounds.size.y < 1f)
            {
                return;
            }

            // This HUD is semantically bottom-left, not merely "somewhere on screen". Pin the
            // visible descendant bounds to that corner every frame. This survives layout rebuilds,
            // nested legacy parents, Retina scaling and runtime resolution changes.
            Rect available = GetSafeCanvasRect(canvasRect, BottomHudMargin);
            Vector2 correction = new Vector2(
                available.xMin - bounds.min.x,
                available.yMin - bounds.min.y);

            // If the panel is unexpectedly too large, prefer keeping its right/top edge visible too.
            if (bounds.size.x <= available.width && bounds.max.x + correction.x > available.xMax)
            {
                correction.x += available.xMax - (bounds.max.x + correction.x);
            }
            if (bounds.size.y <= available.height && bounds.max.y + correction.y > available.yMax)
            {
                correction.y += available.yMax - (bounds.max.y + correction.y);
            }

            if (correction != Vector2.zero)
            {
                Vector3 worldCorrection = canvasRect.TransformVector(new Vector3(correction.x, correction.y, 0f));
                actionRect.position += worldCorrection;
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
            if (_menus == null || _menus.Clock == null || _menus.GameSpeedButton == null ||
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
                    if (_counterRect != null &&
                        _menus.Counter != null &&
                        _menus.Counter.activeInHierarchy)
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
    }
}
