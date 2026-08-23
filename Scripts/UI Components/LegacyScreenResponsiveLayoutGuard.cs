using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Scene-scoped responsive repair for legacy menu screens whose nested layout regions were
    /// authored at 1366x768. RootCanvasCompatibilityGuard owns root viewport/scaler compatibility;
    /// this pass handles nested layout groups that receive a larger allocation from an expanded
    /// root layout but keep reference-sized descendants inside that allocation.
    /// </summary>
    [DefaultExecutionOrder(-750)]
    public sealed class LegacyScreenResponsiveLayoutGuard : MonoBehaviour
    {
        private const float RepairInterval = 0.25f;
        private const float StructuralWidthCoverage = 0.20f;
        private const float StructuralHeightCoverage = 0.20f;
        private const float RelaxedHorizontalMinimumCoverage = 0.20f;
        private const float RelaxedHorizontalDominanceRatio = 1.5f;
        private const float FixedAnchorTolerance = 0.001f;
        private const int MinimumMainMenuControls = 4;
        private const int MaxHierarchyDepth = 16;
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);
        private static readonly Vector2 MainMenuReferenceSize = new Vector2(1366f, 668f);

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private string _sceneName;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _nextRepairTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!IsSupportedScene(scene.name))
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

        private static bool IsSupportedScene(string sceneName)
        {
            return string.Equals(sceneName, "Squad Maker", StringComparison.Ordinal) ||
                   string.Equals(sceneName, "Main Menu", StringComparison.Ordinal);
        }

        private void Initialize(Canvas canvas, string sceneName)
        {
            _canvas = canvas;
            _canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            _sceneName = sceneName;
            _lastScreenWidth = -1;
            _lastScreenHeight = -1;
            ApplyResponsiveRepair();
        }

        private void LateUpdate()
        {
            if (_canvas == null || _canvasRect == null)
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
            ApplyResponsiveRepair();
        }

        private void ApplyResponsiveRepair()
        {
            if (_canvasRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();

            if (string.Equals(_sceneName, "Main Menu", StringComparison.Ordinal))
            {
                ExpandMainMenuInteractiveRoot(_canvasRect);
            }

            RepairNestedStructuralLayouts(_canvasRect, _canvasRect, 0);
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>
        /// The Main Menu's interactive UI lives under one direct canvas branch authored for the
        /// 1366x668 presentation frame. Keep that branch centered and no larger than its authored
        /// reference size. On narrower/shorter canvases it scales down uniformly to fit; on
        /// ultrawide or very tall canvases the extra viewport remains available to the starfield
        /// instead of stretching the green menu frame to fill it.
        /// </summary>
        internal static bool ExpandMainMenuInteractiveRoot(RectTransform canvasRect)
        {
            if (canvasRect == null)
            {
                return false;
            }

            Selectable[] controls = canvasRect.GetComponentsInChildren<Selectable>(true);
            if (controls.Length < MinimumMainMenuControls)
            {
                return false;
            }

            RectTransform candidate = FindDirectCanvasBranch(canvasRect, controls[0].transform as RectTransform);
            if (candidate == null || candidate == canvasRect || candidate.parent is not RectTransform parent ||
                parent.GetComponent<LayoutGroup>() != null)
            {
                return false;
            }

            for (int i = 1; i < controls.Length; i++)
            {
                Transform controlTransform = controls[i] != null ? controls[i].transform : null;
                if (controlTransform != null && !controlTransform.IsChildOf(candidate))
                {
                    return false;
                }
            }

            Vector2 canvasSize = canvasRect.rect.size;
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return false;
            }

            // Do not infer the authored aspect from candidate.rect here. The generic responsive
            // wrapper can temporarily stretch this same legacy branch before this late ownership
            // pass runs. Treating that transient runtime size as the new authored baseline makes
            // repeated resolution changes progressively distort and shrink the menu presentation.
            float authoredAspect = MainMenuReferenceSize.x / MainMenuReferenceSize.y;
            Vector2 availableSize = new Vector2(
                Mathf.Min(canvasSize.x, MainMenuReferenceSize.x),
                Mathf.Min(canvasSize.y, MainMenuReferenceSize.y));
            Vector2 targetSize = FitAspectInside(availableSize, authoredAspect);
            if (targetSize.x <= 0f || targetSize.y <= 0f)
            {
                return false;
            }

            Vector2 centeredAnchor = new Vector2(0.5f, 0.5f);
            bool changed = !Approximately(candidate.anchorMin, centeredAnchor) ||
                           !Approximately(candidate.anchorMax, centeredAnchor) ||
                           !Approximately(candidate.pivot, centeredAnchor) ||
                           !Approximately(candidate.anchoredPosition, Vector2.zero) ||
                           !Approximately(candidate.sizeDelta, targetSize);

            candidate.anchorMin = centeredAnchor;
            candidate.anchorMax = centeredAnchor;
            candidate.pivot = centeredAnchor;
            candidate.anchoredPosition = Vector2.zero;
            candidate.sizeDelta = targetSize;
            return changed;
        }

        private static Vector2 FitAspectInside(Vector2 availableSize, float aspect)
        {
            if (availableSize.x <= 0f || availableSize.y <= 0f || aspect <= 0f)
            {
                return Vector2.zero;
            }

            float width = availableSize.x;
            float height = width / aspect;
            if (height > availableSize.y)
            {
                height = availableSize.y;
                width = height * aspect;
            }

            return new Vector2(width, height);
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return Mathf.Abs(left.x - right.x) <= 0.01f &&
                   Mathf.Abs(left.y - right.y) <= 0.01f;
        }

        private static RectTransform FindDirectCanvasBranch(RectTransform canvasRect, RectTransform descendant)
        {
            if (canvasRect == null || descendant == null || !descendant.IsChildOf(canvasRect))
            {
                return null;
            }

            RectTransform current = descendant;
            while (current.parent is RectTransform parent && parent != canvasRect)
            {
                current = parent;
            }

            return current.parent == canvasRect ? current : null;
        }

        /// <summary>
        /// Runs the existing dominant-axis/cross-axis compatibility rules on nested structural
        /// LayoutGroups as well as root viewport owners. Small local rows are filtered out using
        /// their size relative to the root canvas, so button strips are not treated as screen
        /// structure.
        /// </summary>
        internal static bool RepairNestedStructuralLayouts(
            RectTransform canvasRect,
            RectTransform current,
            int depth)
        {
            if (canvasRect == null || current == null || depth >= MaxHierarchyDepth)
            {
                return false;
            }

            bool changed = false;
            LayoutGroup layout = current.GetComponent<LayoutGroup>();
            if (layout != null && IsStructuralLayout(canvasRect, current))
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(current);
                changed |= RootCanvasCompatibilityGuard.FitDominantVerticalLayoutChild(current);
                changed |= RootCanvasCompatibilityGuard.FitDominantHorizontalLayoutChild(current);
                changed |= FitDominantStructuralHorizontalChild(current);
                changed |= RootCanvasCompatibilityGuard.FitLayoutCrossAxisChildren(current);
                if (changed)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(current);
                }
            }

            for (int i = 0; i < current.childCount; i++)
            {
                RectTransform child = current.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                Canvas nestedCanvas = child.GetComponent<Canvas>();
                if (nestedCanvas != null && nestedCanvas.transform != canvasRect && nestedCanvas.isRootCanvas)
                {
                    continue;
                }

                changed |= RepairNestedStructuralLayouts(canvasRect, child, depth + 1);
            }

            return changed;
        }

        /// <summary>
        /// The Squad Maker's ultrawide root row contains several fixed-width side regions, so its
        /// main work region can be clearly dominant without occupying half of the expanded canvas.
        /// Allow that uniquely dominant region to absorb positive surplus while never shrinking
        /// siblings or treating equal-width control rows as flexible screen structure.
        /// </summary>
        internal static bool FitDominantStructuralHorizontalChild(RectTransform layoutRoot)
        {
            if (layoutRoot == null)
            {
                return false;
            }

            HorizontalLayoutGroup layout = layoutRoot.GetComponent<HorizontalLayoutGroup>();
            if (layout == null || layout.childControlWidth)
            {
                return false;
            }

            RectTransform dominantChild = null;
            float dominantWidth = -1f;
            float secondWidth = -1f;
            float totalChildWidth = 0f;
            int participatingChildren = 0;

            for (int i = 0; i < layoutRoot.childCount; i++)
            {
                RectTransform child = layoutRoot.GetChild(i) as RectTransform;
                if (!CanParticipateInManualLayoutSizing(child) ||
                    Mathf.Abs(child.anchorMax.x - child.anchorMin.x) > FixedAnchorTolerance)
                {
                    continue;
                }

                float width = Mathf.Abs(child.rect.width * child.localScale.x);
                if (width <= 0f)
                {
                    continue;
                }

                participatingChildren++;
                totalChildWidth += width;
                if (width > dominantWidth)
                {
                    secondWidth = dominantWidth;
                    dominantWidth = width;
                    dominantChild = child;
                }
                else if (width > secondWidth)
                {
                    secondWidth = width;
                }
            }

            if (dominantChild == null || participatingChildren < 2)
            {
                return false;
            }

            float availableWidth = layoutRoot.rect.width - layout.padding.left - layout.padding.right;
            if (availableWidth <= 0f ||
                dominantWidth < availableWidth * RelaxedHorizontalMinimumCoverage ||
                (secondWidth > 0f && dominantWidth < secondWidth * RelaxedHorizontalDominanceRatio))
            {
                return false;
            }

            float spacingWidth = layout.spacing * (participatingChildren - 1);
            float fixedOtherWidth = totalChildWidth - dominantWidth;
            float targetScaledWidth = availableWidth - spacingWidth - fixedOtherWidth;
            if (targetScaledWidth <= dominantWidth + 0.01f)
            {
                return false;
            }

            float dominantScale = Mathf.Abs(dominantChild.localScale.x);
            if (dominantScale <= 0.0001f)
            {
                return false;
            }

            dominantChild.SetSizeWithCurrentAnchors(
                RectTransform.Axis.Horizontal,
                targetScaledWidth / dominantScale);
            return true;
        }

        internal static bool IsStructuralLayout(RectTransform canvasRect, RectTransform layoutRoot)
        {
            if (canvasRect == null || layoutRoot == null || layoutRoot.GetComponent<LayoutGroup>() == null)
            {
                return false;
            }

            Vector2 canvasSize = canvasRect.rect.size;
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return false;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, layoutRoot);
            return bounds.size.x >= canvasSize.x * StructuralWidthCoverage &&
                   bounds.size.y >= canvasSize.y * StructuralHeightCoverage;
        }

        private static bool CanParticipateInManualLayoutSizing(RectTransform child)
        {
            if (child == null || !child.gameObject.activeSelf)
            {
                return false;
            }

            LayoutElement layoutElement = child.GetComponent<LayoutElement>();
            return layoutElement == null || !layoutElement.ignoreLayout;
        }
    }
}
