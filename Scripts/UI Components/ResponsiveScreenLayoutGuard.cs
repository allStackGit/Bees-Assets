using Assets.Scripts.UIComponents;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Repairs legacy screen-space UI that was authored inside fixed 1366x768-style wrapper
    /// RectTransforms. CanvasScaler alone cannot make those wrappers responsive: on a 16:10,
    /// 3:2, 4:3 or ultrawide display the root Canvas grows on one axis while fixed wrappers remain
    /// attached to the old reference rectangle.
    ///
    /// This guard runs after GameHudLayoutGuard. It converts screen-relative fixed rectangles to
    /// stretch anchors while preserving their authored edge margins, then lets child layout systems
    /// operate in the actual root-canvas coordinate space. It is scene-agnostic and is installed on
    /// every root screen-space Canvas, including canvases instantiated after scene load.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public sealed class ResponsiveScreenLayoutGuard : MonoBehaviour
    {
        private const float RepairInterval = 0.25f;
        private const float SafeMargin = 8f;
        private const float ReferenceSizeToleranceFraction = 0.01f;
        private const float MinimumReferenceSizeTolerance = 2f;
        private const float FullAxisCoverageThreshold = 0.95f;
        private const float CompanionAxisCoverageThreshold = 0.75f;
        private const float FixedAnchorTolerance = 0.001f;
        private const float RotationToleranceDegrees = 0.01f;
        private const float SquadTabLeftMargin = 10f;
        private const float SquadTabTopMargin = 10f;
        private const float SquadTabGap = 8f;
        private const float ActionBoxMargin = 10f;
        private const int MaxHierarchyDepth = 16;
        private static readonly Vector2 DefaultReferenceResolution = new Vector2(1366f, 768f);

        private static ResponsiveScreenCanvasDiscovery _discovery;

        private Canvas _canvas;
        private CanvasScaler _scaler;
        private RectTransform _canvasRect;
        private GameMenus _menus;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private Rect _lastSafeArea = new Rect(-1f, -1f, -1f, -1f);
        private float _nextRepairTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            EnsureDiscoveryHost();
        }

        private static void EnsureDiscoveryHost()
        {
            if (_discovery != null)
            {
                return;
            }

            GameObject host = new GameObject("Responsive Screen Canvas Discovery");
            host.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            Object.DontDestroyOnLoad(host);
            _discovery = host.AddComponent<ResponsiveScreenCanvasDiscovery>();
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
                for (int i = 0; i < canvases.Length; i++)
                {
                    EnsureCanvasGuard(canvases[i]);
                }
            }
        }

        internal static void EnsureLiveCanvasGuards()
        {
            Canvas[] canvases = Object.FindObjectsByType<Canvas>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < canvases.Length; i++)
            {
                EnsureCanvasGuard(canvases[i]);
            }
        }

        private static void EnsureCanvasGuard(Canvas canvas)
        {
            if (canvas == null || canvas.renderMode == RenderMode.WorldSpace || !canvas.isRootCanvas)
            {
                return;
            }

            ResponsiveScreenLayoutGuard guard = canvas.GetComponent<ResponsiveScreenLayoutGuard>();
            if (guard == null)
            {
                guard = canvas.gameObject.AddComponent<ResponsiveScreenLayoutGuard>();
                guard.Initialize(canvas);
            }
            else if (guard._canvas != canvas)
            {
                guard.Initialize(canvas);
            }
        }

        private void Initialize(Canvas canvas)
        {
            _canvas = canvas;
            _canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            _scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
            if (_canvas != null && _scaler == null)
            {
                _scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            }

            ConfigureScaler();
            ResolveMenus();
            CaptureDisplayState();
            RepairLayout();
        }

        private void Update()
        {
            if (_canvas == null || _canvasRect == null)
            {
                return;
            }

            bool displayChanged = Screen.width != _lastScreenWidth ||
                                  Screen.height != _lastScreenHeight ||
                                  !RectApproximatelyEquals(Screen.safeArea, _lastSafeArea);
            if (displayChanged)
            {
                CaptureDisplayState();
            }

            if (displayChanged || Time.unscaledTime >= _nextRepairTime)
            {
                RepairLayout();
                _nextRepairTime = Time.unscaledTime + RepairInterval;
            }
        }

        private void LateUpdate()
        {
            // GameMenus can activate the selected-squad panel after the periodic hierarchy pass.
            // Pin it after Unity layout has run so a legacy parent/layout component cannot move it
            // below the visible display again in the same frame.
            ResolveMenus();
            PinActionBoxToSafeBottomLeft();
        }

        private void ConfigureScaler()
        {
            if (_scaler == null)
            {
                return;
            }

            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            if (_scaler.referenceResolution.x <= 0f || _scaler.referenceResolution.y <= 0f)
            {
                _scaler.referenceResolution = DefaultReferenceResolution;
            }
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        }

        private Vector2 GetReferenceResolution()
        {
            return _scaler != null &&
                   _scaler.referenceResolution.x > 0f &&
                   _scaler.referenceResolution.y > 0f
                ? _scaler.referenceResolution
                : DefaultReferenceResolution;
        }

        private void CaptureDisplayState()
        {
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            _lastSafeArea = Screen.safeArea;
            ConfigData.ScreenWidth = Screen.width;
            ConfigData.ScreenHeight = Screen.height;
        }

        private void RepairLayout()
        {
            if (_canvas == null || _canvasRect == null || _canvas.renderMode == RenderMode.WorldSpace)
            {
                return;
            }

            ConfigureScaler();
            Canvas.ForceUpdateCanvases();

            Rect safeRect = GetSafeCanvasRect(_canvasRect, SafeMargin);
            RepairHierarchy(_canvasRect, safeRect, GetReferenceResolution(), 0);

            // Stretch conversion changes the coordinate system used by layout groups. Force a
            // layout pass before applying semantic corner placement such as the squad-tab row.
            Canvas.ForceUpdateCanvases();
            ResolveMenus();
            RepairSquadTabs();
            PinActionBoxToSafeBottomLeft();
        }

        private void RepairHierarchy(
            RectTransform parent,
            Rect safeRect,
            Vector2 referenceResolution,
            int depth)
        {
            if (parent == null || depth >= MaxHierarchyDepth)
            {
                return;
            }

            bool parentRepresentsScreen = parent == _canvasRect || IsFullScreenContainer(parent);

            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Canvas childCanvas = child.GetComponent<Canvas>();
                if (childCanvas != null && childCanvas.rootCanvas != _canvas)
                {
                    // A different root Canvas owns its own responsive guard and coordinate system.
                    continue;
                }

                bool repairedScreenRect = parentRepresentsScreen &&
                                          RepairLegacyScreenRect(child, parent, referenceResolution);
                if (repairedScreenRect)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(child);
                }

                if (IsFullScreenContainer(child))
                {
                    RepairHierarchy(child, safeRect, referenceResolution, depth + 1);
                    continue;
                }

                ClampVisibleHierarchyToRect(child, _canvasRect, safeRect);
            }
        }

        /// <summary>
        /// Converts a large fixed screen-relative rectangle to stretch anchors on the axes where it
        /// represents the viewport. Unlike the old exact 1366x768 test, this also handles leaf
        /// backers, 1366x668-style content frames, full-width bars and full-height side containers.
        /// Authored margins are preserved rather than forcing every candidate to become edge-to-edge.
        /// </summary>
        private static bool RepairLegacyScreenRect(
            RectTransform rect,
            RectTransform parent,
            Vector2 referenceResolution)
        {
            if (rect == null || parent == null ||
                referenceResolution.x <= 0f || referenceResolution.y <= 0f ||
                IsFullScreenContainer(rect) ||
                Mathf.Abs(Mathf.DeltaAngle(rect.localEulerAngles.z, 0f)) > RotationToleranceDegrees)
            {
                return false;
            }

            Vector2 size = rect.rect.size;
            float coverageX = Mathf.Abs(size.x * rect.localScale.x) / referenceResolution.x;
            float coverageY = Mathf.Abs(size.y * rect.localScale.y) / referenceResolution.y;
            bool hasFullScreenAxis = coverageX >= FullAxisCoverageThreshold ||
                                     coverageY >= FullAxisCoverageThreshold;
            if (!hasFullScreenAxis)
            {
                return false;
            }

            bool fixedX = Mathf.Abs(rect.anchorMax.x - rect.anchorMin.x) <= FixedAnchorTolerance;
            bool fixedY = Mathf.Abs(rect.anchorMax.y - rect.anchorMin.y) <= FixedAnchorTolerance;
            bool stretchX = fixedX &&
                            Mathf.Approximately(rect.localScale.x, 1f) &&
                            coverageX >= CompanionAxisCoverageThreshold;
            bool stretchY = fixedY &&
                            Mathf.Approximately(rect.localScale.y, 1f) &&
                            coverageY >= CompanionAxisCoverageThreshold;
            if (!stretchX && !stretchY)
            {
                return false;
            }

            Rect localRect = rect.rect;
            float parentReferenceXMin = -parent.pivot.x * referenceResolution.x;
            float parentReferenceXMax = parentReferenceXMin + referenceResolution.x;
            float parentReferenceYMin = -parent.pivot.y * referenceResolution.y;
            float parentReferenceYMax = parentReferenceYMin + referenceResolution.y;

            float anchorReferenceX = Mathf.Lerp(
                parentReferenceXMin,
                parentReferenceXMax,
                rect.anchorMin.x);
            float anchorReferenceY = Mathf.Lerp(
                parentReferenceYMin,
                parentReferenceYMax,
                rect.anchorMin.y);

            float leftMargin = anchorReferenceX + rect.anchoredPosition.x + localRect.xMin -
                               parentReferenceXMin;
            float rightMargin = parentReferenceXMax -
                                (anchorReferenceX + rect.anchoredPosition.x + localRect.xMax);
            float bottomMargin = anchorReferenceY + rect.anchoredPosition.y + localRect.yMin -
                                 parentReferenceYMin;
            float topMargin = parentReferenceYMax -
                              (anchorReferenceY + rect.anchoredPosition.y + localRect.yMax);

            if (stretchX)
            {
                leftMargin = SnapSmallReferenceMargin(leftMargin, referenceResolution.x);
                rightMargin = SnapSmallReferenceMargin(rightMargin, referenceResolution.x);
            }
            if (stretchY)
            {
                bottomMargin = SnapSmallReferenceMargin(bottomMargin, referenceResolution.y);
                topMargin = SnapSmallReferenceMargin(topMargin, referenceResolution.y);
            }

            Vector2 anchorMin = rect.anchorMin;
            Vector2 anchorMax = rect.anchorMax;
            if (stretchX)
            {
                anchorMin.x = 0f;
                anchorMax.x = 1f;
            }
            if (stretchY)
            {
                anchorMin.y = 0f;
                anchorMax.y = 1f;
            }
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;

            Vector2 offsetMin = rect.offsetMin;
            Vector2 offsetMax = rect.offsetMax;
            if (stretchX)
            {
                offsetMin.x = leftMargin;
                offsetMax.x = -rightMargin;
            }
            if (stretchY)
            {
                offsetMin.y = bottomMargin;
                offsetMax.y = -topMargin;
            }
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return true;
        }

        private static float SnapSmallReferenceMargin(float margin, float referenceSize)
        {
            float tolerance = Mathf.Max(
                MinimumReferenceSizeTolerance,
                referenceSize * ReferenceSizeToleranceFraction);
            return Mathf.Abs(margin) <= tolerance ? 0f : margin;
        }

        private static bool IsFullScreenContainer(RectTransform rect)
        {
            if (rect == null)
            {
                return false;
            }

            Vector2 span = rect.anchorMax - rect.anchorMin;
            return span.x >= 0.95f && span.y >= 0.95f;
        }

        private static void StretchToParent(RectTransform rect)
        {
            if (rect == null)
            {
                return;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        private void ResolveMenus()
        {
            if (_menus != null)
            {
                return;
            }

            GameMenus candidate = Object.FindObjectOfType<GameMenus>();
            if (candidate == null)
            {
                return;
            }

            Canvas menuCanvas = null;
            if (candidate.UIOverlay != null)
            {
                menuCanvas = candidate.UIOverlay.GetComponentInParent<Canvas>();
            }
            if (menuCanvas == null)
            {
                menuCanvas = candidate.GetComponentInParent<Canvas>();
            }

            if (menuCanvas != null && menuCanvas.rootCanvas == _canvas)
            {
                _menus = candidate;
            }
        }

        private void RepairSquadTabs()
        {
            if (_menus == null || _menus.Stage == null || _menus.Stage.SquadTabs == null ||
                _menus.Stage.SquadTabs.Count == 0)
            {
                return;
            }

            RectTransform firstTabRect = null;
            for (int i = 0; i < _menus.Stage.SquadTabs.Count; i++)
            {
                SquadTab tab = _menus.Stage.SquadTabs[i];
                if (tab != null && tab.Tab != null)
                {
                    firstTabRect = tab.Tab.GetComponent<RectTransform>();
                    if (firstTabRect != null)
                    {
                        break;
                    }
                }
            }

            RectTransform tabsRoot = firstTabRect != null ? firstTabRect.parent as RectTransform : null;
            if (tabsRoot == null)
            {
                return;
            }

            Canvas tabsCanvas = tabsRoot.GetComponentInParent<Canvas>();
            Canvas rootCanvas = tabsCanvas != null ? tabsCanvas.rootCanvas : null;
            RectTransform canvasRect = rootCanvas != null ? rootCanvas.transform as RectTransform : null;
            if (rootCanvas != _canvas || canvasRect == null)
            {
                return;
            }

            // Space.unity's legacy Squad Tabs root is a centred ~1366x768 RectTransform with a
            // HorizontalLayoutGroup and 200 px left padding. Stretch the parent itself and let its
            // layout group own the children in the real canvas space.
            StretchToParent(tabsRoot);

            HorizontalLayoutGroup layout = tabsRoot.GetComponent<HorizontalLayoutGroup>();
            if (layout == null)
            {
                layout = tabsRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
            }

            Rect fullRect = canvasRect.rect;
            Rect safeRect = GetSafeCanvasRect(canvasRect, 0f);
            int leftPadding = Mathf.RoundToInt(
                Mathf.Max(0f, safeRect.xMin - fullRect.xMin) + SquadTabLeftMargin);
            int topPadding = Mathf.RoundToInt(
                Mathf.Max(0f, fullRect.yMax - safeRect.yMax) + SquadTabTopMargin);

            layout.padding = new RectOffset(leftPadding, 0, topPadding, 0);
            layout.spacing = SquadTabGap;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            for (int i = 0; i < _menus.Stage.SquadTabs.Count; i++)
            {
                SquadTab tab = _menus.Stage.SquadTabs[i];
                if (tab == null || tab.Tab == null)
                {
                    continue;
                }

                RectTransform tabRect = tab.Tab.GetComponent<RectTransform>();
                if (tabRect != null)
                {
                    tabRect.localScale = Vector3.one;
                }
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(tabsRoot);
        }

        private void PinActionBoxToSafeBottomLeft()
        {
            if (_menus == null || _menus.SquadActionBoxUI == null ||
                !_menus.SquadActionBoxUI.activeInHierarchy)
            {
                return;
            }

            RectTransform actionRect = _menus.SquadActionBoxUI.GetComponent<RectTransform>();
            Canvas nearestCanvas = _menus.SquadActionBoxUI.GetComponentInParent<Canvas>();
            Canvas rootCanvas = nearestCanvas != null ? nearestCanvas.rootCanvas : null;
            RectTransform canvasRect = rootCanvas != null ? rootCanvas.transform as RectTransform : null;
            if (actionRect == null || canvasRect == null || actionRect == canvasRect)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, actionRect);
            if (bounds.size.x < 1f || bounds.size.y < 1f)
            {
                return;
            }

            Rect available = GetSafeCanvasRect(canvasRect, ActionBoxMargin);
            Vector2 correction = new Vector2(
                available.xMin - bounds.min.x,
                available.yMin - bounds.min.y);

            if (bounds.size.x <= available.width && bounds.max.x + correction.x > available.xMax)
            {
                correction.x += available.xMax - (bounds.max.x + correction.x);
            }
            if (bounds.size.y <= available.height && bounds.max.y + correction.y > available.yMax)
            {
                correction.y += available.yMax - (bounds.max.y + correction.y);
            }

            if (correction.sqrMagnitude > 0.0001f)
            {
                Vector3 worldCorrection = canvasRect.TransformVector(new Vector3(correction.x, correction.y, 0f));
                actionRect.position += worldCorrection;
            }
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

            Vector2 correction = Vector2.zero;
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

            if (correction.sqrMagnitude > 0.0001f)
            {
                Vector3 worldCorrection = canvasRect.TransformVector(new Vector3(correction.x, correction.y, 0f));
                layoutRoot.position += worldCorrection;
            }
        }

        private static Rect GetSafeCanvasRect(RectTransform canvasRect, float margin)
        {
            Rect full = canvasRect.rect;
            if (Screen.width <= 0 || Screen.height <= 0)
            {
                return InsetRect(full, margin);
            }

            Rect safeArea = Screen.safeArea;
            float xMin = Mathf.Lerp(full.xMin, full.xMax, Mathf.Clamp01(safeArea.xMin / Screen.width));
            float xMax = Mathf.Lerp(full.xMin, full.xMax, Mathf.Clamp01(safeArea.xMax / Screen.width));
            float yMin = Mathf.Lerp(full.yMin, full.yMax, Mathf.Clamp01(safeArea.yMin / Screen.height));
            float yMax = Mathf.Lerp(full.yMin, full.yMax, Mathf.Clamp01(safeArea.yMax / Screen.height));
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

        private static bool RectApproximatelyEquals(Rect a, Rect b)
        {
            return Mathf.Approximately(a.x, b.x) &&
                   Mathf.Approximately(a.y, b.y) &&
                   Mathf.Approximately(a.width, b.width) &&
                   Mathf.Approximately(a.height, b.height);
        }
    }

    /// <summary>
    /// Scene-loaded callbacks do not see root canvases instantiated later by UI prefabs. This tiny
    /// persistent scanner closes that gap without doing an object search every frame.
    /// </summary>
    [DefaultExecutionOrder(-950)]
    internal sealed class ResponsiveScreenCanvasDiscovery : MonoBehaviour
    {
        private const float DiscoveryInterval = 0.5f;
        private float _nextDiscoveryTime;

        private void Update()
        {
            if (Time.unscaledTime < _nextDiscoveryTime)
            {
                return;
            }

            _nextDiscoveryTime = Time.unscaledTime + DiscoveryInterval;
            ResponsiveScreenLayoutGuard.EnsureLiveCanvasGuards();
        }
    }
}
