using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Preserves the authored composition of the legacy Main Menu while adapting its root
    /// presentation to the live viewport. Squad Maker has a separate responsive owner because its
    /// structural work regions must consume viewport surplus instead of remaining inside a bounded
    /// 1366x768 presentation frame.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class LegacyScreenResponsiveLayoutGuard : MonoBehaviour
    {
        private const string MainMenuSceneName = "Main Menu";
        private const string MainMenuPanelName = "MainPanel";
        private const float RepairInterval = 0.25f;
        private const float AnchorTolerance = 0.001f;
        private const float MainMenuViewportHorizontalMargin = 24f;

        // The interactive rows describe the useful Main Menu width, but the authored green panel
        // deliberately leaves breathing room around them and its planet can protrude slightly past
        // the panel edge. Keep that authored breathing room without letting decorative full-width
        // graphics force portrait displays to fit an otherwise empty 1366-wide artboard.
        private const float MainMenuFunctionalHorizontalPadding = 240f;

        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);

        private sealed class RectGeometry
        {
            public RectTransform Rect;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
            public Vector3 LocalScale;
        }

        private sealed class PresentationBranch
        {
            public RectGeometry Root;
            public readonly List<RectGeometry> Descendants = new List<RectGeometry>();
            public bool IsBackdrop;
            public bool ScaleMainMenuVisuals;
            public Vector2 MainMenuPresentationSize;
        }

        private Canvas _canvas;
        private CanvasScaler _scaler;
        private RectTransform _canvasRect;
        private string _sceneName;
        private readonly List<PresentationBranch> _branches = new List<PresentationBranch>();
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _nextRepairTime;
        private bool _restoredAfterCompetingInitialization;

        // Subscribe before the generic BeforeSceneLoad installers. This lets us snapshot the actual
        // authored RectTransform data before any compatibility guard has a chance to rewrite it.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!IsFixedReferencePresentationScene(scene.name))
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas canvas = canvases[i];
                    if (canvas == null || !canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace)
                    {
                        continue;
                    }

                    LegacyScreenResponsiveLayoutGuard guard =
                        canvas.GetComponent<LegacyScreenResponsiveLayoutGuard>();
                    if (guard == null)
                    {
                        guard = canvas.gameObject.AddComponent<LegacyScreenResponsiveLayoutGuard>();
                    }

                    guard.Initialize(canvas, scene.name);
                }
            }
        }

        internal static bool IsFixedReferencePresentationScene(string sceneName)
        {
            return string.Equals(sceneName, MainMenuSceneName, StringComparison.Ordinal);
        }

        private void Initialize(Canvas canvas, string sceneName)
        {
            _canvas = canvas;
            _canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            _scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
            if (_canvas != null && _scaler == null)
            {
                _scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            }

            _sceneName = sceneName;
            ConfigureScaler();
            Canvas.ForceUpdateCanvases();
            CaptureAuthoredBranches();
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            _restoredAfterCompetingInitialization = false;
            ApplyReferencePresentation(restoreDescendants: false);
        }

        private void Update()
        {
            if (_canvas == null || _canvasRect == null)
            {
                return;
            }

            DisableCompetingGeometryGuards();
            ConfigureScaler();

            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
            if (displayChanged)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                ConfigData.ScreenWidth = Screen.width;
                ConfigData.ScreenHeight = Screen.height;
            }

            bool firstStableFrame = !_restoredAfterCompetingInitialization;
            if (!firstStableFrame && !displayChanged && Time.unscaledTime < _nextRepairTime)
            {
                return;
            }

            _nextRepairTime = Time.unscaledTime + RepairInterval;
            CaptureNewDirectBranches();
            ApplyReferencePresentation(firstStableFrame);
            _restoredAfterCompetingInitialization = true;
        }

        private void ConfigureScaler()
        {
            if (_scaler == null)
            {
                return;
            }

            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = ReferenceResolution;
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        }

        private void DisableCompetingGeometryGuards()
        {
            ResponsiveScreenLayoutGuard responsive = _canvas.GetComponent<ResponsiveScreenLayoutGuard>();
            if (responsive != null && responsive.enabled)
            {
                responsive.enabled = false;
            }

            RootCanvasCompatibilityGuard compatibility = _canvas.GetComponent<RootCanvasCompatibilityGuard>();
            if (compatibility != null && compatibility.enabled)
            {
                compatibility.enabled = false;
            }
        }

        private void CaptureAuthoredBranches()
        {
            _branches.Clear();
            CaptureNewDirectBranches();
        }

        private void CaptureNewDirectBranches()
        {
            if (_canvasRect == null)
            {
                return;
            }

            for (int i = 0; i < _canvasRect.childCount; i++)
            {
                RectTransform child = _canvasRect.GetChild(i) as RectTransform;
                if (child == null || IsAlreadyCaptured(child))
                {
                    continue;
                }

                RectGeometry rootGeometry = CaptureGeometry(child);
                PresentationBranch branch = new PresentationBranch
                {
                    Root = rootGeometry,
                    IsBackdrop = IsViewportBackdrop(child)
                };
                CaptureDescendants(child, branch.Descendants);

                if (IsMainMenuPresentationRoot(child))
                {
                    Vector2 presentationSize = CalculateMainMenuPresentationSize(child);
                    if (presentationSize.x > 0f && presentationSize.y > 0f)
                    {
                        branch.ScaleMainMenuVisuals = true;
                        branch.MainMenuPresentationSize = new Vector2(
                            presentationSize.x * Mathf.Abs(rootGeometry.LocalScale.x),
                            presentationSize.y * Mathf.Abs(rootGeometry.LocalScale.y));
                    }
                }

                _branches.Add(branch);
            }
        }

        private bool IsMainMenuPresentationRoot(RectTransform rect)
        {
            return string.Equals(_sceneName, MainMenuSceneName, StringComparison.Ordinal) &&
                   rect != null &&
                   string.Equals(rect.gameObject.name, MainMenuPanelName, StringComparison.Ordinal);
        }

        private bool IsAlreadyCaptured(RectTransform rect)
        {
            for (int i = 0; i < _branches.Count; i++)
            {
                if (_branches[i].Root.Rect == rect)
                {
                    return true;
                }
            }

            return false;
        }

        private static RectGeometry CaptureGeometry(RectTransform rect)
        {
            return new RectGeometry
            {
                Rect = rect,
                AnchorMin = rect.anchorMin,
                AnchorMax = rect.anchorMax,
                Pivot = rect.pivot,
                AnchoredPosition = rect.anchoredPosition,
                SizeDelta = rect.sizeDelta,
                LocalScale = rect.localScale
            };
        }

        private static void CaptureDescendants(RectTransform parent, List<RectGeometry> destination)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                destination.Add(CaptureGeometry(child));
                CaptureDescendants(child, destination);
            }
        }

        private static bool IsViewportBackdrop(RectTransform branch)
        {
            if (branch == null || branch.GetComponentInChildren<Selectable>(true) != null ||
                branch.GetComponentInChildren<LayoutGroup>(true) != null)
            {
                return false;
            }

            string objectName = branch.gameObject.name;
            bool namedBackdrop = objectName.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 objectName.IndexOf("backer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 objectName.IndexOf("starfield", StringComparison.OrdinalIgnoreCase) >= 0;
            bool fullStretch = Approximately(branch.anchorMin, Vector2.zero) &&
                               Approximately(branch.anchorMax, Vector2.one);
            return namedBackdrop || fullStretch;
        }

        private void ApplyReferencePresentation(bool restoreDescendants)
        {
            if (_canvasRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            Vector2 canvasSize = _canvasRect.rect.size;
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return;
            }

            for (int i = 0; i < _branches.Count; i++)
            {
                PresentationBranch branch = _branches[i];
                if (branch.Root.Rect == null)
                {
                    continue;
                }

                if (restoreDescendants)
                {
                    for (int j = 0; j < branch.Descendants.Count; j++)
                    {
                        RestoreGeometry(branch.Descendants[j]);
                    }
                }

                if (branch.IsBackdrop)
                {
                    StretchBackdrop(branch.Root.Rect);
                }
                else
                {
                    ApplyReferenceRootGeometry(branch.Root, canvasSize);
                    if (branch.ScaleMainMenuVisuals)
                    {
                        ApplyMainMenuVisualScale(branch, canvasSize);
                    }
                }

                if (branch.Root.Rect.GetComponent<LayoutGroup>() != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(branch.Root.Rect);
                }
            }

            Canvas.ForceUpdateCanvases();
        }

        private static void ApplyMainMenuVisualScale(PresentationBranch branch, Vector2 canvasSize)
        {
            if (branch == null || branch.Root == null || branch.Root.Rect == null)
            {
                return;
            }

            float multiplier = CalculateMainMenuPresentationScale(canvasSize, branch.MainMenuPresentationSize);
            Vector3 authoredScale = branch.Root.LocalScale;
            branch.Root.Rect.localScale = new Vector3(
                authoredScale.x * multiplier,
                authoredScale.y * multiplier,
                authoredScale.z);
        }

        /// <summary>
        /// CanvasScaler.Expand keeps the complete 1366x768 authoring frame visible. That is useful
        /// for coordinate mapping, but on portrait displays it also makes the Main Menu inherit a
        /// width-driven scale from hundreds of units of empty horizontal artboard. Grow the menu
        /// uniformly inside that logical canvas until either its functional width, its authored
        /// height, or reference-height tracking reaches a viewport boundary.
        /// </summary>
        internal static float CalculateMainMenuPresentationScale(
            Vector2 canvasSize,
            Vector2 presentationSize)
        {
            if (canvasSize.x <= 0f || canvasSize.y <= 0f ||
                presentationSize.x <= 0f || presentationSize.y <= 0f)
            {
                return 1f;
            }

            float availableWidth = Mathf.Max(1f, canvasSize.x - MainMenuViewportHorizontalMargin * 2f);
            float heightTrackingScale = canvasSize.y / ReferenceResolution.y;
            float widthFitScale = availableWidth / presentationSize.x;
            float heightFitScale = canvasSize.y / presentationSize.y;
            return Mathf.Max(0.01f, Mathf.Min(heightTrackingScale, widthFitScale, heightFitScale));
        }

        /// <summary>
        /// Measures the Main Menu from controls, not from decorative Graphics. MainPanel's
        /// RectTransform is 1366 units wide and some sprites can also contain transparent/full-width
        /// padding, so either would reproduce the portrait-thumbnail bug if used as the fitting
        /// boundary. Selectables capture the functional composition reliably, while fixed authored
        /// padding preserves the surrounding green panel/decorative breathing room. Vertical sizing
        /// remains the authored MainPanel height so the composition never grows just because its
        /// buttons occupy only the middle portion of the panel.
        /// </summary>
        private static Vector2 CalculateMainMenuPresentationSize(RectTransform root)
        {
            if (root == null)
            {
                return Vector2.zero;
            }

            bool hasBounds = false;
            float minX = 0f;
            float maxX = 0f;
            float minY = 0f;
            float maxY = 0f;

            Selectable[] selectables = root.GetComponentsInChildren<Selectable>(true);
            for (int i = 0; i < selectables.Length; i++)
            {
                RectTransform selectableRect = selectables[i] != null
                    ? selectables[i].transform as RectTransform
                    : null;
                if (selectableRect == null)
                {
                    continue;
                }

                EncapsulateRect(
                    root,
                    selectableRect,
                    selectableRect.rect,
                    ref hasBounds,
                    ref minX,
                    ref maxX,
                    ref minY,
                    ref maxY);
            }

            float authoredHeight = Mathf.Abs(root.rect.height);
            if (!hasBounds)
            {
                return new Vector2(Mathf.Abs(root.rect.width), authoredHeight);
            }

            float halfFunctionalWidth = Mathf.Max(Mathf.Abs(minX), Mathf.Abs(maxX));
            float functionalWidth = halfFunctionalWidth * 2f + MainMenuFunctionalHorizontalPadding;
            return new Vector2(functionalWidth, authoredHeight);
        }

        internal static Vector2 CalculateMainMenuPresentationSizeForTest(RectTransform root)
        {
            return CalculateMainMenuPresentationSize(root);
        }

        private static void EncapsulateRect(
            RectTransform root,
            RectTransform rectTransform,
            Rect localRect,
            ref bool hasBounds,
            ref float minX,
            ref float maxX,
            ref float minY,
            ref float maxY)
        {
            EncapsulatePoint(root, rectTransform, new Vector2(localRect.xMin, localRect.yMin),
                ref hasBounds, ref minX, ref maxX, ref minY, ref maxY);
            EncapsulatePoint(root, rectTransform, new Vector2(localRect.xMin, localRect.yMax),
                ref hasBounds, ref minX, ref maxX, ref minY, ref maxY);
            EncapsulatePoint(root, rectTransform, new Vector2(localRect.xMax, localRect.yMin),
                ref hasBounds, ref minX, ref maxX, ref minY, ref maxY);
            EncapsulatePoint(root, rectTransform, new Vector2(localRect.xMax, localRect.yMax),
                ref hasBounds, ref minX, ref maxX, ref minY, ref maxY);
        }

        private static void EncapsulatePoint(
            RectTransform root,
            RectTransform rectTransform,
            Vector2 localPoint,
            ref bool hasBounds,
            ref float minX,
            ref float maxX,
            ref float minY,
            ref float maxY)
        {
            Vector3 worldPoint = rectTransform.TransformPoint(new Vector3(localPoint.x, localPoint.y, 0f));
            Vector3 rootPoint = root.InverseTransformPoint(worldPoint);
            if (!hasBounds)
            {
                minX = maxX = rootPoint.x;
                minY = maxY = rootPoint.y;
                hasBounds = true;
                return;
            }

            minX = Mathf.Min(minX, rootPoint.x);
            maxX = Mathf.Max(maxX, rootPoint.x);
            minY = Mathf.Min(minY, rootPoint.y);
            maxY = Mathf.Max(maxY, rootPoint.y);
        }

        private static void RestoreGeometry(RectGeometry geometry)
        {
            if (geometry == null || geometry.Rect == null)
            {
                return;
            }

            geometry.Rect.anchorMin = geometry.AnchorMin;
            geometry.Rect.anchorMax = geometry.AnchorMax;
            geometry.Rect.pivot = geometry.Pivot;
            geometry.Rect.anchoredPosition = geometry.AnchoredPosition;
            geometry.Rect.sizeDelta = geometry.SizeDelta;
            geometry.Rect.localScale = geometry.LocalScale;
        }

        private static void ApplyReferenceRootGeometry(RectGeometry geometry, Vector2 canvasSize)
        {
            if (geometry == null || geometry.Rect == null)
            {
                return;
            }

            geometry.Rect.anchorMin = MapReferenceAnchor(geometry.AnchorMin, canvasSize);
            geometry.Rect.anchorMax = MapReferenceAnchor(geometry.AnchorMax, canvasSize);
            geometry.Rect.pivot = geometry.Pivot;
            geometry.Rect.anchoredPosition = geometry.AnchoredPosition;
            geometry.Rect.sizeDelta = geometry.SizeDelta;
            geometry.Rect.localScale = geometry.LocalScale;
        }

        internal static Vector2 MapReferenceAnchor(Vector2 authoredAnchor, Vector2 canvasSize)
        {
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return authoredAnchor;
            }

            Vector2 referenceOrigin = (canvasSize - ReferenceResolution) * 0.5f;
            return new Vector2(
                (referenceOrigin.x + authoredAnchor.x * ReferenceResolution.x) / canvasSize.x,
                (referenceOrigin.y + authoredAnchor.y * ReferenceResolution.y) / canvasSize.y);
        }

        internal static bool ApplyReferenceGeometryForTest(
            RectTransform rect,
            Vector2 canvasSize,
            Vector2 authoredAnchorMin,
            Vector2 authoredAnchorMax,
            Vector2 authoredPosition,
            Vector2 authoredSize)
        {
            if (rect == null)
            {
                return false;
            }

            Vector2 mappedMin = MapReferenceAnchor(authoredAnchorMin, canvasSize);
            Vector2 mappedMax = MapReferenceAnchor(authoredAnchorMax, canvasSize);
            bool changed = !Approximately(rect.anchorMin, mappedMin) ||
                           !Approximately(rect.anchorMax, mappedMax) ||
                           !Approximately(rect.anchoredPosition, authoredPosition) ||
                           !Approximately(rect.sizeDelta, authoredSize) ||
                           !Approximately(rect.localScale, Vector3.one);

            rect.anchorMin = mappedMin;
            rect.anchorMax = mappedMax;
            rect.anchoredPosition = authoredPosition;
            rect.sizeDelta = authoredSize;
            rect.localScale = Vector3.one;
            return changed;
        }

        internal static float CalculateBackdropCoverScale(Vector2 viewportSize, Vector2 sourceSize)
        {
            if (viewportSize.x <= 0f || viewportSize.y <= 0f ||
                sourceSize.x <= 0f || sourceSize.y <= 0f)
            {
                return 1f;
            }

            float viewportAspect = viewportSize.x / viewportSize.y;
            float sourceAspect = sourceSize.x / sourceSize.y;
            return Mathf.Max(1f, Mathf.Max(viewportAspect / sourceAspect, sourceAspect / viewportAspect));
        }

        /// <summary>
        /// A viewport backdrop must cover with the rendered graphic, not merely with its
        /// RectTransform. Unity Image.preserveAspect uses contain semantics, which letterboxes a
        /// square starfield inside ultrawide or portrait canvases even after the RectTransform is
        /// stretched. Scale preserve-aspect Images just enough to envelope the viewport instead.
        /// </summary>
        internal static bool StretchBackdrop(RectTransform rect)
        {
            if (rect == null)
            {
                return false;
            }

            RectTransform parent = rect.parent as RectTransform;
            Vector2 viewportSize = parent != null ? parent.rect.size : rect.rect.size;
            float coverScale = 1f;
            Image image = rect.GetComponent<Image>();
            if (image != null && image.preserveAspect && image.sprite != null)
            {
                coverScale = CalculateBackdropCoverScale(viewportSize, image.sprite.rect.size);
            }

            Vector3 targetScale = new Vector3(coverScale, coverScale, 1f);
            bool changed = !Approximately(rect.anchorMin, Vector2.zero) ||
                           !Approximately(rect.anchorMax, Vector2.one) ||
                           !Approximately(rect.offsetMin, Vector2.zero) ||
                           !Approximately(rect.offsetMax, Vector2.zero) ||
                           !Approximately(rect.localScale, targetScale);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = targetScale;
            return changed;
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return Mathf.Abs(left.x - right.x) <= AnchorTolerance &&
                   Mathf.Abs(left.y - right.y) <= AnchorTolerance;
        }

        private static bool Approximately(Vector3 left, Vector3 right)
        {
            return Mathf.Abs(left.x - right.x) <= AnchorTolerance &&
                   Mathf.Abs(left.y - right.y) <= AnchorTolerance &&
                   Mathf.Abs(left.z - right.z) <= AnchorTolerance;
        }
    }
}
