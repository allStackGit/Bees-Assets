using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Owns Squad Maker-specific responsive behavior. Unlike the bounded Main Menu presentation,
    /// Squad Maker is a screen-filling work surface: viewport-scale containers expand to the live
    /// canvas, structural layout groups absorb aspect-ratio surplus, and small local control rows
    /// retain their authored sizing. The START/TEST hover descriptions are also kept out of layout
    /// measurement so they cannot displace level details.
    /// </summary>
    [DefaultExecutionOrder(-700)]
    public sealed class SquadMakerResponsiveLayoutGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const float RepairInterval = 0.25f;
        private const float StructuralWidthCoverage = 0.20f;
        private const float StructuralHeightCoverage = 0.20f;
        private const float RelaxedHorizontalMinimumCoverage = 0.20f;
        private const float RelaxedHorizontalDominanceRatio = 1.5f;
        private const float FixedAnchorTolerance = 0.001f;
        private const int MaxHierarchyDepth = 16;
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);

        private SquadMaker _squadMaker;
        private Canvas _canvas;
        private RectTransform _canvasRect;
        private SquadMakerHoverDescriptionRelay _startRelay;
        private SquadMakerHoverDescriptionRelay _testRelay;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _nextRepairTime;
        private bool _legacyReferenceMappingRestored;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (scene.name != SquadMakerSceneName)
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                SquadMaker squadMaker = root.GetComponentInChildren<SquadMaker>(true);
                if (squadMaker == null)
                {
                    continue;
                }

                SquadMakerResponsiveLayoutGuard guard =
                    squadMaker.GetComponent<SquadMakerResponsiveLayoutGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerResponsiveLayoutGuard>();
                }
                guard.Initialize(squadMaker);
                return;
            }
        }

        private void Awake()
        {
            if (_squadMaker == null)
            {
                Initialize(GetComponent<SquadMaker>());
            }
        }

        private void Initialize(SquadMaker squadMaker)
        {
            _squadMaker = squadMaker;
            if (_squadMaker == null)
            {
                return;
            }

            Canvas localCanvas = _squadMaker.GetComponentInParent<Canvas>();
            _canvas = localCanvas != null ? localCanvas.rootCanvas : null;
            _canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;
            _lastScreenWidth = -1;
            _lastScreenHeight = -1;
            _nextRepairTime = 0f;

            TakeViewportOwnership();
            StabilizeHoverDescriptions();
            ApplyViewportFill();
        }

        private void LateUpdate()
        {
            // The authored pointer-exit callbacks still call SetActive(false). Restore the
            // description object before Unity's canvas/layout rebuild for this frame, while its
            // LayoutElement keeps it out of the measured column.
            StabilizeHoverDescriptions();

            if (_canvas == null || _canvasRect == null)
            {
                Canvas localCanvas = _squadMaker != null ? _squadMaker.GetComponentInParent<Canvas>() : null;
                _canvas = localCanvas != null ? localCanvas.rootCanvas : null;
                _canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;
                if (_canvasRect == null)
                {
                    return;
                }
            }

            TakeViewportOwnership();

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
            ApplyViewportFill();
        }

        /// <summary>
        /// Main Menu intentionally uses LegacyScreenResponsiveLayoutGuard's bounded reference
        /// presentation. Squad Maker does not: it owns the full viewport. Disable that competing
        /// fixed-artboard pass here, restore any reference-anchor mapping it already applied during
        /// scene initialization, and leave the generic viewport guards enabled for screen owners.
        /// </summary>
        private void TakeViewportOwnership()
        {
            if (_canvas == null || _canvasRect == null)
            {
                return;
            }

            LegacyScreenResponsiveLayoutGuard legacy =
                _canvas.GetComponent<LegacyScreenResponsiveLayoutGuard>();
            if (legacy != null)
            {
                legacy.enabled = false;
                if (!_legacyReferenceMappingRestored)
                {
                    RestoreLegacyReferenceMappedDirectAnchors(_canvasRect);
                    _legacyReferenceMappingRestored = true;
                }
            }

            // These installers are idempotent and also cover canvases created after scene load.
            ResponsiveScreenLayoutGuard.EnsureLiveCanvasGuards();
            RootCanvasCompatibilityGuard.EnsureLiveCanvasGuards();

            ResponsiveScreenLayoutGuard responsive = _canvas.GetComponent<ResponsiveScreenLayoutGuard>();
            if (responsive != null)
            {
                responsive.enabled = true;
            }

            RootCanvasCompatibilityGuard compatibility = _canvas.GetComponent<RootCanvasCompatibilityGuard>();
            if (compatibility != null)
            {
                compatibility.enabled = true;
            }
        }

        private void ApplyViewportFill()
        {
            if (_canvasRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            StretchReferenceViewportBranches(_canvasRect);
            RepairNestedStructuralLayouts(_canvasRect, _canvasRect, 0);
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>
        /// LegacyScreenResponsiveLayoutGuard maps every non-backdrop direct branch into a centered
        /// 1366x768 artboard. Invert that mapping before handing Squad Maker back to viewport layout.
        /// Full-stretch branches are already the desired viewport geometry and are left untouched.
        /// </summary>
        internal static bool RestoreLegacyReferenceMappedDirectAnchors(RectTransform canvasRect)
        {
            if (canvasRect == null)
            {
                return false;
            }

            Vector2 canvasSize = canvasRect.rect.size;
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return false;
            }

            bool changed = false;
            for (int i = 0; i < canvasRect.childCount; i++)
            {
                RectTransform child = canvasRect.GetChild(i) as RectTransform;
                if (child == null || HasFullStretchAnchors(child))
                {
                    continue;
                }

                Vector2 restoredMin = UnmapReferenceAnchor(child.anchorMin, canvasSize);
                Vector2 restoredMax = UnmapReferenceAnchor(child.anchorMax, canvasSize);
                if (!Approximately(child.anchorMin, restoredMin) ||
                    !Approximately(child.anchorMax, restoredMax))
                {
                    child.anchorMin = restoredMin;
                    child.anchorMax = restoredMax;
                    changed = true;
                }
            }

            return changed;
        }

        internal static Vector2 UnmapReferenceAnchor(Vector2 mappedAnchor, Vector2 canvasSize)
        {
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return mappedAnchor;
            }

            Vector2 referenceOrigin = (canvasSize - ReferenceResolution) * 0.5f;
            return new Vector2(
                (mappedAnchor.x * canvasSize.x - referenceOrigin.x) / ReferenceResolution.x,
                (mappedAnchor.y * canvasSize.y - referenceOrigin.y) / ReferenceResolution.y);
        }

        /// <summary>
        /// Explicitly hand reference-sized direct screen owners the entire live canvas. This is the
        /// key contract that prevents ultrawide/portrait Squad Maker from becoming a centered
        /// 1366x768 island with blank space around it.
        /// </summary>
        internal static bool StretchReferenceViewportBranches(RectTransform canvasRect)
        {
            if (canvasRect == null)
            {
                return false;
            }

            bool changed = false;
            for (int i = 0; i < canvasRect.childCount; i++)
            {
                RectTransform child = canvasRect.GetChild(i) as RectTransform;
                if (child == null ||
                    !RootCanvasCompatibilityGuard.RectRepresentsViewport(child, canvasRect, ReferenceResolution))
                {
                    continue;
                }

                bool ownsScreenStructure = child.GetComponentInChildren<LayoutGroup>(true) != null;
                bool isBacker = IsScreenBacker(child);
                if (!ownsScreenStructure && !isBacker)
                {
                    continue;
                }

                changed |= RootCanvasCompatibilityGuard.StretchToParent(child);
            }

            return changed;
        }

        private static bool IsScreenBacker(RectTransform rect)
        {
            if (rect == null || rect.GetComponent<Image>() == null)
            {
                return false;
            }

            string objectName = rect.gameObject.name;
            return objectName.IndexOf("backer", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   objectName.IndexOf("background", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   objectName.IndexOf("panel", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Runs the established dominant-axis/cross-axis compatibility rules on nested structural
        /// LayoutGroups. Large work regions absorb live aspect-ratio surplus; small button strips do
        /// not, so individual controls retain their authored proportions while the screen structure
        /// fills the viewport.
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
        /// The Squad Maker's wide root rows contain several fixed side regions and one clearly
        /// dominant work region. Let that work region absorb positive horizontal surplus without
        /// stretching equal-width local controls or shrinking the side regions below their authored
        /// sizes.
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

        private static bool HasFullStretchAnchors(RectTransform rect)
        {
            return rect != null &&
                   Approximately(rect.anchorMin, Vector2.zero) &&
                   Approximately(rect.anchorMax, Vector2.one);
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return Mathf.Abs(left.x - right.x) <= 0.001f &&
                   Mathf.Abs(left.y - right.y) <= 0.001f;
        }

        private void StabilizeHoverDescriptions()
        {
            if (_squadMaker == null)
            {
                return;
            }

            StabilizeDescription(
                _squadMaker.StartButton,
                _squadMaker.StartText,
                ref _startRelay);
            StabilizeDescription(
                _squadMaker.TestButton,
                _squadMaker.TestText,
                ref _testRelay);
        }

        private static void StabilizeDescription(
            GameObject button,
            GameObject description,
            ref SquadMakerHoverDescriptionRelay relay)
        {
            if (button == null || description == null)
            {
                return;
            }

            if (!button.activeSelf)
            {
                if (description.activeSelf)
                {
                    description.SetActive(false);
                }
                relay?.ResetHover();
                return;
            }

            if (relay == null || relay.gameObject != button)
            {
                relay = button.GetComponent<SquadMakerHoverDescriptionRelay>();
                if (relay == null)
                {
                    relay = button.AddComponent<SquadMakerHoverDescriptionRelay>();
                }
                relay.Configure(description);
            }

            SetDescriptionVisibility(description, relay.IsHovered);
        }

        internal static void SetDescriptionVisibility(GameObject description, bool visible)
        {
            if (description == null)
            {
                return;
            }

            // These are overlays, not structural rows. Keeping them active avoids hover-time
            // layout rebuilds; ignoring them in LayoutGroups prevents one or two invisible help
            // objects from consuming the space needed by the level title/details.
            LayoutElement layoutElement = description.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = description.AddComponent<LayoutElement>();
            }
            layoutElement.ignoreLayout = true;

            if (!description.activeSelf)
            {
                description.SetActive(true);
            }

            CanvasGroup group = description.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = description.AddComponent<CanvasGroup>();
            }

            group.alpha = visible ? 1f : 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }
    }

    internal sealed class SquadMakerHoverDescriptionRelay : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler
    {
        private GameObject _description;

        internal bool IsHovered { get; private set; }

        internal void Configure(GameObject description)
        {
            _description = description;
            IsHovered = false;
            SquadMakerResponsiveLayoutGuard.SetDescriptionVisibility(_description, false);
        }

        internal void ResetHover()
        {
            IsHovered = false;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            IsHovered = true;
            SquadMakerResponsiveLayoutGuard.SetDescriptionVisibility(_description, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            IsHovered = false;
            SquadMakerResponsiveLayoutGuard.SetDescriptionVisibility(_description, false);
        }
    }
}
