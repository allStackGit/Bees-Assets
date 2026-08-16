using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Final compatibility pass for legacy screen-space UI after responsive wrapper repair and
    /// semantic gameplay HUD layout have run. This guard deliberately operates at ownership
    /// boundaries only: viewport-level LayoutGroup owners/backers and direct root-canvas
    /// interactive islands. It must not translate arbitrary nested UI; gameplay-specific placement
    /// such as the scoreboard/squad-tab relationship belongs to GameHudLayoutGuard.
    /// </summary>
    [DefaultExecutionOrder(-800)]
    public sealed class RootCanvasCompatibilityGuard : MonoBehaviour
    {
        private const float RepairInterval = 0.25f;
        private const float ScreenCoverageThreshold = 0.90f;
        private const float FullAnchorThreshold = 0.95f;
        private const float FixedAnchorTolerance = 0.001f;
        private const float NavigationControlMargin = 15f;
        private const int MaxHierarchyDepth = 16;
        private static readonly Vector2 DefaultReferenceResolution = new Vector2(1366f, 768f);

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private CanvasScaler _scaler;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _nextRepairTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
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
            Canvas[] canvases = UnityEngine.Object.FindObjectsByType<Canvas>(
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

            RootCanvasCompatibilityGuard guard = canvas.GetComponent<RootCanvasCompatibilityGuard>();
            if (guard == null)
            {
                guard = canvas.gameObject.AddComponent<RootCanvasCompatibilityGuard>();
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
            _lastScreenWidth = -1;
            _lastScreenHeight = -1;
            ApplyCompatibilityLayout();
        }

        private void LateUpdate()
        {
            if (_canvas == null || _canvasRect == null || _canvas.renderMode == RenderMode.WorldSpace)
            {
                return;
            }

            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
            if (displayChanged)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
            }

            if (!displayChanged && Time.unscaledTime < _nextRepairTime)
            {
                return;
            }

            _nextRepairTime = Time.unscaledTime + RepairInterval;
            ApplyCompatibilityLayout();
        }

        private void ApplyCompatibilityLayout()
        {
            if (_canvas == null || _canvasRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            RepairViewportOwners(_canvasRect, GetReferenceResolution(), 0);
            ClampDirectInteractiveIslands(_canvasRect, GetReferenceResolution());
            Canvas.ForceUpdateCanvases();
        }

        private Vector2 GetReferenceResolution()
        {
            return _scaler != null &&
                   _scaler.referenceResolution.x > 0f &&
                   _scaler.referenceResolution.y > 0f
                ? _scaler.referenceResolution
                : DefaultReferenceResolution;
        }

        private void RepairViewportOwners(RectTransform parent, Vector2 referenceResolution, int depth)
        {
            if (parent == null || depth >= MaxHierarchyDepth)
            {
                return;
            }

            bool parentRepresentsViewport = parent == _canvasRect ||
                                            RectCoversReferenceScreen(parent, referenceResolution) ||
                                            HasFullStretchAnchors(parent);

            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                Canvas childCanvas = child.GetComponent<Canvas>();
                if (childCanvas != null && childCanvas.rootCanvas != _canvas)
                {
                    continue;
                }

                if (parentRepresentsViewport && parent.GetComponent<LayoutGroup>() == null)
                {
                    LayoutGroup layout = child.GetComponent<LayoutGroup>();
                    bool layoutOwner = layout != null;
                    bool screenBacker = IsScreenBacker(child);
                    bool looksScreenSized = HasFullStretchAnchors(child) ||
                                            RectCoversReferenceScreen(child, referenceResolution);

                    if ((layoutOwner || screenBacker) && looksScreenSized)
                    {
                        StretchToParent(child);
                        if (layoutOwner)
                        {
                            LayoutRebuilder.ForceRebuildLayoutImmediate(child);
                            FitDominantVerticalLayoutChild(child);
                            LayoutRebuilder.ForceRebuildLayoutImmediate(child);
                        }
                    }
                }

                if (HasFullStretchAnchors(child) || RectCoversReferenceScreen(child, referenceResolution))
                {
                    RepairViewportOwners(child, referenceResolution, depth + 1);
                }
            }
        }

        private static bool IsScreenBacker(RectTransform rect)
        {
            if (rect == null || rect.GetComponent<Image>() == null)
            {
                return false;
            }

            string objectName = rect.gameObject.name;
            return objectName.IndexOf("backer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   objectName.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool StretchToParent(RectTransform rect)
        {
            if (rect == null || rect.parent is not RectTransform parent ||
                parent.GetComponent<LayoutGroup>() != null)
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

        /// <summary>
        /// Gives viewport-height surplus to the dominant fixed-height child of a VerticalLayoutGroup.
        /// This preserves fixed footer/tool rows while making the main screen body absorb the extra
        /// logical height introduced by CanvasScaler.Expand on taller displays.
        /// </summary>
        internal static bool FitDominantVerticalLayoutChild(RectTransform layoutRoot)
        {
            if (layoutRoot == null)
            {
                return false;
            }

            VerticalLayoutGroup layout = layoutRoot.GetComponent<VerticalLayoutGroup>();
            if (layout == null || layout.childControlHeight)
            {
                return false;
            }

            RectTransform dominantChild = null;
            float dominantHeight = -1f;
            float totalChildHeight = 0f;
            int participatingChildren = 0;

            for (int i = 0; i < layoutRoot.childCount; i++)
            {
                RectTransform child = layoutRoot.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf)
                {
                    continue;
                }

                LayoutElement layoutElement = child.GetComponent<LayoutElement>();
                if (layoutElement != null && layoutElement.ignoreLayout)
                {
                    continue;
                }

                if (Mathf.Abs(child.anchorMax.y - child.anchorMin.y) > FixedAnchorTolerance)
                {
                    continue;
                }

                float height = Mathf.Abs(child.rect.height * child.localScale.y);
                if (height <= 0f)
                {
                    continue;
                }

                participatingChildren++;
                totalChildHeight += height;
                if (height > dominantHeight)
                {
                    dominantHeight = height;
                    dominantChild = child;
                }
            }

            if (dominantChild == null || participatingChildren < 2)
            {
                return false;
            }

            float availableHeight = layoutRoot.rect.height - layout.padding.top - layout.padding.bottom;
            if (availableHeight <= 0f || dominantHeight < availableHeight * 0.5f)
            {
                return false;
            }

            float spacingHeight = layout.spacing * (participatingChildren - 1);
            float fixedOtherHeight = totalChildHeight - dominantHeight;
            float targetScaledHeight = availableHeight - spacingHeight - fixedOtherHeight;
            float dominantScale = Mathf.Abs(dominantChild.localScale.y);
            if (dominantScale <= 0.0001f)
            {
                return false;
            }

            float targetHeight = targetScaledHeight / dominantScale;
            if (targetHeight <= 0f || Mathf.Abs(targetHeight - dominantChild.rect.height) < 0.01f)
            {
                return false;
            }

            dominantChild.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetHeight);
            return true;
        }

        private static void ClampDirectInteractiveIslands(
            RectTransform canvasRect,
            Vector2 referenceResolution)
        {
            if (canvasRect == null)
            {
                return;
            }

            for (int i = 0; i < canvasRect.childCount; i++)
            {
                RectTransform child = canvasRect.GetChild(i) as RectTransform;
                if (child == null || child.GetComponent<LayoutGroup>() != null ||
                    HasFullStretchAnchors(child) || RectCoversReferenceScreen(child, referenceResolution))
                {
                    continue;
                }

                Canvas nestedCanvas = child.GetComponent<Canvas>();
                if (nestedCanvas != null && nestedCanvas.isRootCanvas)
                {
                    continue;
                }

                if (child.GetComponentInChildren<Selectable>(true) == null)
                {
                    continue;
                }

                float margin = RequiresNavigationMargin(child) ? NavigationControlMargin : 0f;
                ClampIslandToCanvasWithMargin(child, canvasRect, margin);
            }
        }

        private static bool RequiresNavigationMargin(RectTransform island)
        {
            if (island == null)
            {
                return false;
            }

            string objectName = island.gameObject.name;
            return objectName == "Back Button" ||
                   objectName == "Continue Button" ||
                   objectName == "Skip Button";
        }

        internal static bool ClampIslandToCanvas(RectTransform island, RectTransform canvasRect)
        {
            return ClampIslandToCanvasWithMargin(island, canvasRect, 0f);
        }

        internal static bool ClampIslandToCanvasWithMargin(
            RectTransform island,
            RectTransform canvasRect,
            float margin)
        {
            if (island == null || canvasRect == null)
            {
                return false;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, island);
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
            island.position += worldCorrection;
            return true;
        }

        private static bool HasFullStretchAnchors(RectTransform rect)
        {
            if (rect == null)
            {
                return false;
            }

            Vector2 span = rect.anchorMax - rect.anchorMin;
            return span.x >= FullAnchorThreshold && span.y >= FullAnchorThreshold;
        }

        private static bool RectCoversReferenceScreen(RectTransform rect, Vector2 referenceResolution)
        {
            if (rect == null || referenceResolution.x <= 0f || referenceResolution.y <= 0f)
            {
                return false;
            }

            Vector2 size = rect.rect.size;
            float coverageX = Mathf.Abs(size.x * rect.localScale.x) / referenceResolution.x;
            float coverageY = Mathf.Abs(size.y * rect.localScale.y) / referenceResolution.y;
            return coverageX >= ScreenCoverageThreshold && coverageY >= ScreenCoverageThreshold;
        }
    }
}
