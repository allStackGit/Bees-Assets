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
    /// This guard deliberately repairs only screen-sized/container geometry. It does not translate
    /// arbitrary UI islands or reinterpret semantic HUD layout; authored sibling relationships must
    /// remain intact, while GameHudLayoutGuard owns the few gameplay controls whose screen-edge
    /// relationship is explicitly known.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public sealed class ResponsiveScreenLayoutGuard : MonoBehaviour
    {
        private const float RepairInterval = 0.25f;
        private const float ReferenceSizeToleranceFraction = 0.01f;
        private const float MinimumReferenceSizeTolerance = 2f;
        private const float FullAxisCoverageThreshold = 0.95f;
        private const float CompanionAxisCoverageThreshold = 0.75f;
        private const float FixedAnchorTolerance = 0.001f;
        private const float RotationToleranceDegrees = 0.01f;
        private const int MaxHierarchyDepth = 16;
        private static readonly Vector2 DefaultReferenceResolution = new Vector2(1366f, 768f);

        private static ResponsiveScreenCanvasDiscovery _discovery;

        private Canvas _canvas;
        private CanvasScaler _scaler;
        private RectTransform _canvasRect;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
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
            CaptureDisplayState();
            RepairLayout();
        }

        private void Update()
        {
            if (_canvas == null || _canvasRect == null)
            {
                return;
            }

            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
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
            RepairHierarchy(_canvasRect, GetReferenceResolution(), 0);
            Canvas.ForceUpdateCanvases();
        }

        private void RepairHierarchy(RectTransform parent, Vector2 referenceResolution, int depth)
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

                // Recurse only through containers that actually represent the viewport. Do not
                // clamp or translate ordinary children: doing that broke authored relationships
                // such as Squad Maker's level text relative to the START/TEST button group.
                if (IsFullScreenContainer(child))
                {
                    RepairHierarchy(child, referenceResolution, depth + 1);
                }
            }
        }

        /// <summary>
        /// Converts a large fixed screen-relative rectangle to stretch anchors on the axes where it
        /// represents the viewport. This handles leaf backers, 1366x668-style content frames,
        /// full-width bars and full-height side containers while preserving authored margins.
        /// LayoutGroup-owned children are deliberately excluded: Unity's layout pass owns their
        /// anchors, positions and sizes, so rewriting them here can invalidate sibling/footer layout.
        /// </summary>
        private static bool RepairLegacyScreenRect(
            RectTransform rect,
            RectTransform parent,
            Vector2 referenceResolution)
        {
            if (rect == null || parent == null ||
                referenceResolution.x <= 0f || referenceResolution.y <= 0f ||
                IsFullScreenContainer(rect) ||
                parent.GetComponent<LayoutGroup>() != null ||
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
    }

    /// <summary>
    /// Root canvases can be instantiated after SceneManager.sceneLoaded. A lightweight persistent
    /// host periodically discovers them so every screen-space root gets the same wrapper and
    /// final ownership-boundary compatibility passes.
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
            RootCanvasCompatibilityGuard.EnsureLiveCanvasGuards();
        }
    }
}
