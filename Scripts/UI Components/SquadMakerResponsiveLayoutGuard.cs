using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Owns Squad Maker-specific responsive behavior. The authored Squad Maker is a 1366x768
    /// composition made from several independent direct Canvas branches rather than one reliable
    /// full-screen wrapper. At non-16:9 sizes those branches must keep their authored normalized
    /// positions and extents across the live viewport; fitting the entire 1366x768 composition
    /// inside the viewport merely produces a centered 16:9 island and blank bands.
    ///
    /// The SquadMaker controller lives on the scene's separate UI Manager root, not beneath the
    /// visible Squad Maker Canvas. Canvas ownership therefore comes from a serialized UI reference
    /// (ChosenSquadList) rather than from the controller's transform ancestry.
    ///
    /// This guard snapshots the authored direct-branch geometry plus the fixed sizes of children
    /// owned by screen-scale structural LayoutGroups. Every responsive pass restores those canonical
    /// structural sizes before deriving the live allocation, so a previously repaired narrow/tall
    /// viewport can never become the baseline for the next resize. START/TEST hover descriptions
    /// remain visual overlays and never participate in structural layout measurement.
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

        private sealed class DirectBranchReferenceGeometry
        {
            public RectTransform Rect;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
            public Vector3 LocalScale;
        }

        private sealed class StructuralChildReferenceGeometry
        {
            public RectTransform Rect;
            public Vector2 SizeDelta;
        }

        private SquadMaker _squadMaker;
        private Canvas _canvas;
        private CanvasScaler _scaler;
        private RectTransform _canvasRect;
        private SquadMakerHoverDescriptionRelay _startRelay;
        private SquadMakerHoverDescriptionRelay _testRelay;
        private readonly List<DirectBranchReferenceGeometry> _referenceBranches =
            new List<DirectBranchReferenceGeometry>();
        private readonly List<StructuralChildReferenceGeometry> _structuralChildReferences =
            new List<StructuralChildReferenceGeometry>();
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _nextRepairTime;
        private bool _referenceGeometryCaptured;
        private bool _structuralReferenceGeometryCaptured;

        // Subscribe before the generic BeforeSceneLoad responsive guards. The specialized Squad
        // Maker owner must capture its canonical layout sizes before a generic compatibility pass
        // has any opportunity to resize those same structural children.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
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

        internal static Canvas ResolveOwnedCanvas(SquadMaker squadMaker)
        {
            if (squadMaker == null)
            {
                return null;
            }

            // In the authored Squad Maker scene the controller is an added component on the
            // top-level UI Manager prefab, while ChosenSquadList is inside the IntroPopup Canvas.
            // Looking upward from the controller therefore returns no Canvas and makes the entire
            // specialized responsive pass a no-op. Resolve from a stable serialized UI anchor.
            Canvas localCanvas = squadMaker.ChosenSquadList != null
                ? squadMaker.ChosenSquadList.GetComponentInParent<Canvas>()
                : null;

            // Retain the hierarchy lookup as a fallback for isolated test fixtures or future scenes
            // that intentionally place the controller beneath its UI Canvas.
            if (localCanvas == null)
            {
                localCanvas = squadMaker.GetComponentInParent<Canvas>();
            }

            return localCanvas != null ? localCanvas.rootCanvas : null;
        }

        private void Initialize(SquadMaker squadMaker)
        {
            _squadMaker = squadMaker;
            if (_squadMaker == null)
            {
                return;
            }

            _canvas = ResolveOwnedCanvas(_squadMaker);
            _canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;
            _scaler = _canvas != null ? _canvas.GetComponent<CanvasScaler>() : null;
            if (_canvas != null && _scaler == null)
            {
                _scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            }

            _lastScreenWidth = -1;
            _lastScreenHeight = -1;
            _nextRepairTime = 0f;
            _referenceGeometryCaptured = false;
            _structuralReferenceGeometryCaptured = false;

            PrepareReferenceGeometry();
            StabilizeHoverDescriptions();
            ApplyViewportFill();
        }

        private void LateUpdate()
        {
            StabilizeHoverDescriptions();

            if (_canvas == null || _canvasRect == null)
            {
                _canvas = ResolveOwnedCanvas(_squadMaker);
                _canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;
                _scaler = _canvas != null ? _canvas.GetComponent<CanvasScaler>() : null;
                if (_canvasRect == null)
                {
                    return;
                }
            }

            DisableCompetingGeometryGuards();
            ConfigureScaler();

            if (!_referenceGeometryCaptured)
            {
                PrepareReferenceGeometry();
            }

            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
            if (displayChanged)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                ConfigData.ScreenWidth = Screen.width;
                ConfigData.ScreenHeight = Screen.height;
            }

            if (!displayChanged && Time.unscaledTime < _nextRepairTime)
            {
                return;
            }

            _nextRepairTime = Time.unscaledTime + RepairInterval;
            ApplyViewportFill();
        }

        private void PrepareReferenceGeometry()
        {
            if (_canvas == null || _canvasRect == null)
            {
                return;
            }

            // LegacyScreenResponsiveLayoutGuard also subscribes before the generic guards and may
            // already have mapped direct branches into a centered reference artboard. Undo that one
            // mapping before capturing the real authored geometry. Nested structural sizes remain
            // canonical because both specialized owners run before the generic compatibility passes.
            LegacyScreenResponsiveLayoutGuard legacy =
                _canvas.GetComponent<LegacyScreenResponsiveLayoutGuard>();
            if (legacy != null)
            {
                legacy.enabled = false;
                RestoreLegacyReferenceMappedDirectAnchors(_canvasRect);
            }

            DisableCompetingGeometryGuards();
            ConfigureScaler();
            Canvas.ForceUpdateCanvases();
            CaptureDirectReferenceBranches();
            CaptureStructuralChildReferenceGeometry();
            _referenceGeometryCaptured = _referenceBranches.Count > 0;
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
            if (_canvas == null)
            {
                return;
            }

            LegacyScreenResponsiveLayoutGuard legacy =
                _canvas.GetComponent<LegacyScreenResponsiveLayoutGuard>();
            if (legacy != null && legacy.enabled)
            {
                legacy.enabled = false;
            }

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

        private void CaptureDirectReferenceBranches()
        {
            _referenceBranches.Clear();
            if (_canvasRect == null)
            {
                return;
            }

            for (int i = 0; i < _canvasRect.childCount; i++)
            {
                RectTransform child = _canvasRect.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                _referenceBranches.Add(new DirectBranchReferenceGeometry
                {
                    Rect = child,
                    AnchorMin = child.anchorMin,
                    AnchorMax = child.anchorMax,
                    Pivot = child.pivot,
                    AnchoredPosition = child.anchoredPosition,
                    SizeDelta = child.sizeDelta,
                    LocalScale = child.localScale
                });
            }
        }

        private void CaptureStructuralChildReferenceGeometry()
        {
            _structuralChildReferences.Clear();
            if (_canvasRect == null)
            {
                _structuralReferenceGeometryCaptured = false;
                return;
            }

            CaptureStructuralChildReferenceGeometry(_canvasRect, 0);
            _structuralReferenceGeometryCaptured = true;
        }

        private void CaptureStructuralChildReferenceGeometry(RectTransform current, int depth)
        {
            if (_canvasRect == null || current == null || depth >= MaxHierarchyDepth)
            {
                return;
            }

            LayoutGroup layout = current.GetComponent<LayoutGroup>();
            if (layout != null && IsReferenceStructuralLayout(_canvasRect, current))
            {
                for (int i = 0; i < current.childCount; i++)
                {
                    RectTransform child = current.GetChild(i) as RectTransform;
                    if (child == null || IsStructuralChildReferenceCaptured(child))
                    {
                        continue;
                    }

                    _structuralChildReferences.Add(new StructuralChildReferenceGeometry
                    {
                        Rect = child,
                        SizeDelta = child.sizeDelta
                    });
                }
            }

            for (int i = 0; i < current.childCount; i++)
            {
                RectTransform child = current.GetChild(i) as RectTransform;
                if (child != null)
                {
                    CaptureStructuralChildReferenceGeometry(child, depth + 1);
                }
            }
        }

        private bool IsStructuralChildReferenceCaptured(RectTransform rect)
        {
            for (int i = 0; i < _structuralChildReferences.Count; i++)
            {
                if (_structuralChildReferences[i].Rect == rect)
                {
                    return true;
                }
            }

            return false;
        }

        private void RestoreStructuralChildReferenceGeometry()
        {
            for (int i = 0; i < _structuralChildReferences.Count; i++)
            {
                StructuralChildReferenceGeometry geometry = _structuralChildReferences[i];
                if (geometry == null || geometry.Rect == null)
                {
                    continue;
                }

                geometry.Rect.sizeDelta = geometry.SizeDelta;
            }
        }

        private void ApplyViewportFill()
        {
            if (_canvasRect == null || !_referenceGeometryCaptured)
            {
                return;
            }

            if (!_structuralReferenceGeometryCaptured)
            {
                CaptureStructuralChildReferenceGeometry();
            }

            // Never let the result of a previous responsive pass become input to the next pass.
            // The fitting helpers intentionally inspect current child sizes to identify dominant
            // regions, so restoring canonical structural sizes is what makes resize behavior
            // reversible and prevents progressive shrink after repeated aspect changes.
            RestoreStructuralChildReferenceGeometry();
            Canvas.ForceUpdateCanvases();
            for (int i = 0; i < _referenceBranches.Count; i++)
            {
                DirectBranchReferenceGeometry branch = _referenceBranches[i];
                if (branch == null || branch.Rect == null)
                {
                    continue;
                }

                ApplyReferenceProportionalGeometry(
                    branch.Rect,
                    branch.AnchorMin,
                    branch.AnchorMax,
                    branch.Pivot,
                    branch.AnchoredPosition,
                    branch.SizeDelta,
                    branch.LocalScale);
            }

            Canvas.ForceUpdateCanvases();
            RepairNestedStructuralLayouts(_canvasRect, _canvasRect, 0);
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>
        /// Converts an authored RectTransform rectangle from the 1366x768 coordinate plane into
        /// normalized screen bounds. Unlike preserving its original fixed anchor plus pixel offset,
        /// this carries both position and extent proportionally onto any live aspect ratio.
        /// </summary>
        internal static Rect CalculateNormalizedReferenceRect(
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta)
        {
            Vector2 anchorReference = new Vector2(
                Mathf.Lerp(anchorMin.x, anchorMax.x, pivot.x) * ReferenceResolution.x,
                Mathf.Lerp(anchorMin.y, anchorMax.y, pivot.y) * ReferenceResolution.y);
            Vector2 pivotReference = anchorReference + anchoredPosition;
            Vector2 referenceSize = new Vector2(
                (anchorMax.x - anchorMin.x) * ReferenceResolution.x + sizeDelta.x,
                (anchorMax.y - anchorMin.y) * ReferenceResolution.y + sizeDelta.y);
            Vector2 referenceMin = pivotReference - Vector2.Scale(pivot, referenceSize);

            return new Rect(
                referenceMin.x / ReferenceResolution.x,
                referenceMin.y / ReferenceResolution.y,
                referenceSize.x / ReferenceResolution.x,
                referenceSize.y / ReferenceResolution.y);
        }

        internal static bool ApplyReferenceProportionalGeometry(
            RectTransform rect,
            Vector2 authoredAnchorMin,
            Vector2 authoredAnchorMax,
            Vector2 authoredPivot,
            Vector2 authoredPosition,
            Vector2 authoredSizeDelta,
            Vector3 authoredScale)
        {
            if (rect == null)
            {
                return false;
            }

            Rect normalized = CalculateNormalizedReferenceRect(
                authoredAnchorMin,
                authoredAnchorMax,
                authoredPivot,
                authoredPosition,
                authoredSizeDelta);
            Vector2 targetMin = new Vector2(normalized.xMin, normalized.yMin);
            Vector2 targetMax = new Vector2(normalized.xMax, normalized.yMax);
            bool changed = !Approximately(rect.anchorMin, targetMin) ||
                           !Approximately(rect.anchorMax, targetMax) ||
                           !Approximately(rect.pivot, authoredPivot) ||
                           !Approximately(rect.anchoredPosition, Vector2.zero) ||
                           !Approximately(rect.sizeDelta, Vector2.zero) ||
                           !Approximately(rect.localScale, authoredScale);

            rect.anchorMin = targetMin;
            rect.anchorMax = targetMax;
            rect.pivot = authoredPivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = authoredScale;
            return changed;
        }

        /// <summary>
        /// LegacyScreenResponsiveLayoutGuard maps direct anchors into a centered 1366x768 logical
        /// artboard. Reverse that transform before reference-space geometry is captured.
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

        internal static bool IsReferenceStructuralLayout(RectTransform canvasRect, RectTransform layoutRoot)
        {
            if (canvasRect == null || layoutRoot == null || layoutRoot.GetComponent<LayoutGroup>() == null)
            {
                return false;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, layoutRoot);
            return bounds.size.x >= ReferenceResolution.x * StructuralWidthCoverage &&
                   bounds.size.y >= ReferenceResolution.y * StructuralHeightCoverage;
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

        private static bool Approximately(Vector3 left, Vector3 right)
        {
            return Mathf.Abs(left.x - right.x) <= 0.001f &&
                   Mathf.Abs(left.y - right.y) <= 0.001f &&
                   Mathf.Abs(left.z - right.z) <= 0.001f;
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
