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
        private const string SquadMakerMainContainerName = "Main Container";
        private const string SquadMakerWorkColumnName = "Squad Maker Column";
        private static readonly Vector2 MainMenuReferenceSize = new Vector2(1366f, 668f);

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private RectTransform _squadMakerMainContainer;
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
            _squadMakerMainContainer = string.Equals(sceneName, "Squad Maker", StringComparison.Ordinal)
                ? FindDescendantByName(_canvasRect, SquadMakerMainContainerName)
                : null;
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
            else if (string.Equals(_sceneName, "Squad Maker", StringComparison.Ordinal))
            {
                RepairSquadMakerMainContainer(_squadMakerMainContainer);
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

        /// <summary>
        /// The Squad Maker's top-level row is authored as three fixed-width columns whose widths
        /// total 1366. Its HorizontalLayoutGroup also has childForceExpandWidth enabled while
        /// childControlWidth is disabled. Unity therefore expands the invisible layout cells but
        /// leaves the visible columns at their old widths, exposing equal strips of the blue Main
        /// Container between them on wide displays. Normalize that contradictory ownership and
        /// assign all live horizontal surplus (or deficit) to the central Squad Maker work column.
        /// The root columns and the direct Squads sub-columns also fill the live height so 718px
        /// authored regions do not detach from their owners on very tall displays.
        /// </summary>
        internal static bool RepairSquadMakerMainContainer(RectTransform mainContainer)
        {
            if (mainContainer == null ||
                !string.Equals(mainContainer.gameObject.name, SquadMakerMainContainerName, StringComparison.Ordinal))
            {
                return false;
            }

            HorizontalLayoutGroup layout = mainContainer.GetComponent<HorizontalLayoutGroup>();
            if (layout == null || layout.childControlWidth)
            {
                return false;
            }

            bool changed = false;
            if (layout.childForceExpandWidth)
            {
                layout.childForceExpandWidth = false;
                changed = true;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(mainContainer);
            changed |= FitNamedHorizontalLayoutChild(mainContainer, SquadMakerWorkColumnName);
            changed |= FitStructuralHorizontalCrossAxisChildren(mainContainer);

            // The scene's right-hand Squads Column is itself a direct horizontal structural row
            // (Saved Squads + Chosen Squads). Repair only direct child rows here rather than scanning
            // the entire UI hierarchy every quarter second.
            for (int i = 0; i < mainContainer.childCount; i++)
            {
                RectTransform child = mainContainer.GetChild(i) as RectTransform;
                if (child != null && child.GetComponent<HorizontalLayoutGroup>() != null)
                {
                    changed |= FitStructuralHorizontalCrossAxisChildren(child);
                }
            }

            if (changed)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(mainContainer);
            }

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

        private static RectTransform FindDescendantByName(RectTransform root, string objectName)
        {
            if (root == null || string.IsNullOrEmpty(objectName))
            {
                return null;
            }

            if (string.Equals(root.gameObject.name, objectName, StringComparison.Ordinal))
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                RectTransform child = root.GetChild(i) as RectTransform;
                RectTransform match = FindDescendantByName(child, objectName);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
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

        /// <summary>
        /// Sizes one explicitly owned horizontal work region to the space left after its fixed
        /// siblings. Unlike the generic dominance heuristic, this works in both directions so a
        /// column enlarged on an ultrawide display returns to its authored width when the viewport
        /// returns to 16:9.
        /// </summary>
        internal static bool FitNamedHorizontalLayoutChild(RectTransform layoutRoot, string childName)
        {
            if (layoutRoot == null || string.IsNullOrEmpty(childName))
            {
                return false;
            }

            HorizontalLayoutGroup layout = layoutRoot.GetComponent<HorizontalLayoutGroup>();
            if (layout == null || layout.childControlWidth)
            {
                return false;
            }

            RectTransform flexibleChild = null;
            float fixedOtherWidth = 0f;
            int participatingChildren = 0;

            for (int i = 0; i < layoutRoot.childCount; i++)
            {
                RectTransform child = layoutRoot.GetChild(i) as RectTransform;
                if (!CanParticipateInManualLayoutSizing(child) ||
                    Mathf.Abs(child.anchorMax.x - child.anchorMin.x) > FixedAnchorTolerance)
                {
                    continue;
                }

                float scale = Mathf.Abs(child.localScale.x);
                float width = Mathf.Abs(child.rect.width * scale);
                if (scale <= 0.0001f || width <= 0f)
                {
                    continue;
                }

                participatingChildren++;
                if (string.Equals(child.gameObject.name, childName, StringComparison.Ordinal))
                {
                    flexibleChild = child;
                }
                else
                {
                    fixedOtherWidth += width;
                }
            }

            if (flexibleChild == null || participatingChildren < 2)
            {
                return false;
            }

            float availableWidth = layoutRoot.rect.width - layout.padding.left - layout.padding.right;
            float spacingWidth = layout.spacing * (participatingChildren - 1);
            float targetScaledWidth = availableWidth - spacingWidth - fixedOtherWidth;
            float flexibleScale = Mathf.Abs(flexibleChild.localScale.x);
            if (targetScaledWidth <= 0f || flexibleScale <= 0.0001f)
            {
                return false;
            }

            float targetWidth = targetScaledWidth / flexibleScale;
            if (Mathf.Abs(targetWidth - flexibleChild.rect.width) < 0.01f)
            {
                return false;
            }

            flexibleChild.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
            return true;
        }

        /// <summary>
        /// Structural horizontal rows represent screen columns, not local cards. Once their owner
        /// receives additional height on a tall display, every participating fixed-anchor column
        /// must fill that live cross-axis. Comparing an old 718px child against the already-expanded
        /// parent would otherwise stop recognizing it on sufficiently tall viewports.
        /// </summary>
        internal static bool FitStructuralHorizontalCrossAxisChildren(RectTransform layoutRoot)
        {
            if (layoutRoot == null)
            {
                return false;
            }

            HorizontalLayoutGroup layout = layoutRoot.GetComponent<HorizontalLayoutGroup>();
            if (layout == null || layout.childControlHeight)
            {
                return false;
            }

            float availableHeight = layoutRoot.rect.height - layout.padding.top - layout.padding.bottom;
            if (availableHeight <= 0f)
            {
                return false;
            }

            bool changed = false;
            for (int i = 0; i < layoutRoot.childCount; i++)
            {
                RectTransform child = layoutRoot.GetChild(i) as RectTransform;
                if (!CanParticipateInManualLayoutSizing(child) ||
                    Mathf.Abs(child.anchorMax.y - child.anchorMin.y) > FixedAnchorTolerance)
                {
                    continue;
                }

                float scale = Mathf.Abs(child.localScale.y);
                if (scale <= 0.0001f)
                {
                    continue;
                }

                float targetHeight = availableHeight / scale;
                if (Mathf.Abs(targetHeight - child.rect.height) < 0.01f)
                {
                    continue;
                }

                child.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetHeight);
                changed = true;
            }

            return changed;
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
