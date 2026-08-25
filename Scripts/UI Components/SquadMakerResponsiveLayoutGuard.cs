using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Owns Squad Maker-specific responsive behavior.
    ///
    /// The authored screen is a 1366x768 desktop composition. MainPanel is viewport-sized, while
    /// its body/footer and the body's three columns are ordinary Unity LayoutGroup children. The
    /// responsive owner therefore changes the native layout contract rather than repeatedly writing
    /// LayoutGroup-owned RectTransforms. The footer and side columns keep their authored dimensions,
    /// the center work column absorbs horizontal surplus, and the body absorbs vertical surplus.
    ///
    /// The SquadMaker controller is on a separate UI Manager root. The visible hierarchy is resolved
    /// from the serialized ChosenSquadList reference so this guard always operates on the same
    /// IntroPopup/MainPanel instance that is actually rendered.
    /// </summary>
    [DefaultExecutionOrder(-700)]
    public sealed class SquadMakerResponsiveLayoutGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string MainPanelName = "MainPanel";
        private const string MainContainerName = "Main Container";
        private const string FooterName = "Footer";
        private const string ShipSelectorColumnName = "Ship Selector Column";
        private const string SquadMakerColumnName = "Squad Maker Column";
        private const string SquadSettingsName = "Squad Settings";
        private const string SquadCompositionName = "Squad Composition";
        private const string SquadsColumnName = "Squads Column";
        private const string SavedSquadsColumnName = "Saved Squads Column";
        private const string ChosenSquadsColumnName = "Chosen Squads Column";
        private const float RepairInterval = 0.25f;
        private const float StructuralWidthCoverage = 0.20f;
        private const float StructuralHeightCoverage = 0.20f;
        private const float RelaxedHorizontalMinimumCoverage = 0.20f;
        private const float RelaxedHorizontalDominanceRatio = 1.5f;
        private const float FixedAnchorTolerance = 0.001f;
        private const float ChosenScrollHeightTolerance = 0.01f;
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
            public Vector2 Size;
        }

        private sealed class SquadMakerLayoutReferenceGeometry
        {
            public RectTransform MainPanel;
            public RectTransform MainContainer;
            public RectTransform Footer;
            public RectTransform ShipSelectorColumn;
            public RectTransform SquadMakerColumn;
            public RectTransform SquadSettings;
            public RectTransform SquadComposition;
            public RectTransform SquadsColumn;
            public RectTransform SavedSquadsColumn;
            public RectTransform ChosenSquadsColumn;
            public RectTransform ChosenSquadScroll;
            public Vector2 MainContainerSize;
            public Vector2 FooterSize;
            public Vector2 ShipSelectorColumnSize;
            public Vector2 SquadMakerColumnSize;
            public Vector2 SquadSettingsSize;
            public Vector2 SquadCompositionSize;
            public Vector2 SquadsColumnSize;
            public Vector2 SavedSquadsColumnSize;
            public Vector2 ChosenSquadsColumnSize;
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
        private SquadMakerLayoutReferenceGeometry _layoutReference;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _nextRepairTime;
        private float _chosenSquadScrollReferenceHeight = -1f;
        private float _lastAppliedChosenSquadScrollHeight = -1f;
        private bool _referenceGeometryCaptured;
        private bool _structuralReferenceGeometryCaptured;
        private bool _warnedMissingAuthoredHierarchy;

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

            Canvas localCanvas = squadMaker.ChosenSquadList != null
                ? squadMaker.ChosenSquadList.GetComponentInParent<Canvas>()
                : null;

            if (localCanvas == null)
            {
                localCanvas = squadMaker.GetComponentInParent<Canvas>();
            }

            return localCanvas != null ? localCanvas.rootCanvas : null;
        }

        private void Initialize(SquadMaker squadMaker)
        {
            if (squadMaker == null)
            {
                return;
            }

            Canvas ownedCanvas = ResolveOwnedCanvas(squadMaker);

            // AddComponent invokes Awake synchronously, and the scene-loaded bootstrap calls
            // Initialize again immediately afterward. Once the same owner/canvas has captured its
            // immutable authored geometry, that second call must be a no-op; otherwise already-
            // responsive RectTransforms become the new baseline and later display changes drift.
            if (_squadMaker == squadMaker &&
                _referenceGeometryCaptured &&
                _canvas != null &&
                _canvas == ownedCanvas)
            {
                return;
            }

            _squadMaker = squadMaker;
            _canvas = ownedCanvas;
            _canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;
            _scaler = _canvas != null ? _canvas.GetComponent<CanvasScaler>() : null;
            if (_canvas != null && _scaler == null)
            {
                _scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            }

            _lastScreenWidth = -1;
            _lastScreenHeight = -1;
            _nextRepairTime = 0f;
            _chosenSquadScrollReferenceHeight = -1f;
            _lastAppliedChosenSquadScrollHeight = -1f;
            _referenceGeometryCaptured = false;
            _structuralReferenceGeometryCaptured = false;
            _layoutReference = null;
            _warnedMissingAuthoredHierarchy = false;

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
            CaptureSquadMakerLayoutReferenceGeometry();

            // Generic structural snapshots are fallback-only. Capturing/restoring them on the real
            // Squad Maker hierarchy would create a second writer for LayoutGroup-owned children.
            if (!HasAuthoredLayoutReference())
            {
                CaptureStructuralChildReferenceGeometry();
            }
            else
            {
                _structuralChildReferences.Clear();
                _structuralReferenceGeometryCaptured = false;
            }

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
                        Size = child.rect.size
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

                SetRectSize(geometry.Rect, geometry.Size);
            }
        }

        private void CaptureSquadMakerLayoutReferenceGeometry()
        {
            _layoutReference = null;
            if (_canvasRect == null)
            {
                return;
            }

            RectTransform mainPanel = null;
            RectTransform mainContainer = null;
            RectTransform footer = null;
            RectTransform shipSelectorColumn = null;
            RectTransform squadMakerColumn = null;
            RectTransform squadSettings = null;
            RectTransform squadComposition = null;
            RectTransform squadsColumn = null;
            RectTransform savedSquadsColumn = null;
            RectTransform chosenSquadsColumn = null;
            RectTransform chosenSquadScroll = null;

            // Production path: walk upward from the serialized right-side list. This avoids choosing
            // a similarly named object from another Canvas/prefab branch.
            RectTransform chosenList = _squadMaker != null && _squadMaker.ChosenSquadList != null
                ? _squadMaker.ChosenSquadList.transform as RectTransform
                : null;
            if (chosenList != null)
            {
                chosenSquadsColumn = FindAncestorByName(chosenList, ChosenSquadsColumnName);
                squadsColumn = FindAncestorByName(chosenSquadsColumn, SquadsColumnName);
                mainContainer = FindAncestorByName(squadsColumn, MainContainerName);
                mainPanel = FindAncestorByName(mainContainer, MainPanelName);

                if (mainPanel != null && mainContainer != null && squadsColumn != null &&
                    chosenSquadsColumn != null)
                {
                    footer = FindDirectChildByName(mainPanel, FooterName);
                    shipSelectorColumn = FindDirectChildByName(mainContainer, ShipSelectorColumnName);
                    squadMakerColumn = FindDirectChildByName(mainContainer, SquadMakerColumnName);
                    savedSquadsColumn = FindDirectChildByName(squadsColumn, SavedSquadsColumnName);
                    chosenSquadScroll = FindDirectChildAncestor(chosenList, chosenSquadsColumn);
                    squadSettings = FindDirectChildByName(squadMakerColumn, SquadSettingsName);
                    squadComposition = FindDirectChildByName(squadMakerColumn, SquadCompositionName);
                }
            }

            // Fallback for isolated tests/future fixtures that intentionally do not provide a
            // SquadMaker serialized anchor.
            if (mainPanel == null || mainContainer == null || footer == null ||
                shipSelectorColumn == null || squadMakerColumn == null || squadSettings == null ||
                squadComposition == null || squadsColumn == null || savedSquadsColumn == null ||
                chosenSquadsColumn == null)
            {
                mainPanel = FindDescendantByName(_canvasRect, MainPanelName);
                mainContainer = FindOwnedChild(mainPanel, MainContainerName);
                footer = FindOwnedChild(mainPanel, FooterName);
                shipSelectorColumn = FindOwnedChild(mainContainer, ShipSelectorColumnName);
                squadMakerColumn = FindOwnedChild(mainContainer, SquadMakerColumnName);
                squadSettings = FindOwnedChild(squadMakerColumn, SquadSettingsName);
                squadComposition = FindOwnedChild(squadMakerColumn, SquadCompositionName);
                squadsColumn = FindOwnedChild(mainContainer, SquadsColumnName);
                savedSquadsColumn = FindOwnedChild(squadsColumn, SavedSquadsColumnName);
                chosenSquadsColumn = FindOwnedChild(squadsColumn, ChosenSquadsColumnName);
                if (chosenList != null && chosenSquadsColumn != null)
                {
                    chosenSquadScroll = FindDirectChildAncestor(chosenList, chosenSquadsColumn);
                }
            }

            if (mainPanel == null || mainContainer == null || footer == null ||
                shipSelectorColumn == null || squadMakerColumn == null || squadSettings == null ||
                squadComposition == null || squadsColumn == null || savedSquadsColumn == null ||
                chosenSquadsColumn == null)
            {
                WarnMissingAuthoredHierarchy();
                return;
            }

            Canvas resolvedCanvas = mainPanel.GetComponentInParent<Canvas>();
            if (_canvas != null && resolvedCanvas != null && resolvedCanvas.rootCanvas != _canvas)
            {
                WarnMissingAuthoredHierarchy();
                return;
            }

            _layoutReference = new SquadMakerLayoutReferenceGeometry
            {
                MainPanel = mainPanel,
                MainContainer = mainContainer,
                Footer = footer,
                ShipSelectorColumn = shipSelectorColumn,
                SquadMakerColumn = squadMakerColumn,
                SquadSettings = squadSettings,
                SquadComposition = squadComposition,
                SquadsColumn = squadsColumn,
                SavedSquadsColumn = savedSquadsColumn,
                ChosenSquadsColumn = chosenSquadsColumn,
                ChosenSquadScroll = chosenSquadScroll,
                MainContainerSize = mainContainer.rect.size,
                FooterSize = footer.rect.size,
                ShipSelectorColumnSize = shipSelectorColumn.rect.size,
                SquadMakerColumnSize = squadMakerColumn.rect.size,
                SquadSettingsSize = squadSettings.rect.size,
                SquadCompositionSize = squadComposition.rect.size,
                SquadsColumnSize = squadsColumn.rect.size,
                SavedSquadsColumnSize = savedSquadsColumn.rect.size,
                ChosenSquadsColumnSize = chosenSquadsColumn.rect.size
            };
        }

        private void WarnMissingAuthoredHierarchy()
        {
            if (_warnedMissingAuthoredHierarchy || _squadMaker == null)
            {
                return;
            }

            _warnedMissingAuthoredHierarchy = true;
            Debug.LogWarning(
                "Squad Maker responsive layout could not resolve the authored MainPanel/body/column " +
                "hierarchy from ChosenSquadList; falling back to generic structural repair.");
        }

        private bool HasAuthoredLayoutReference()
        {
            SquadMakerLayoutReferenceGeometry reference = _layoutReference;
            return reference != null &&
                   reference.MainPanel != null &&
                   reference.MainContainer != null &&
                   reference.Footer != null &&
                   reference.ShipSelectorColumn != null &&
                   reference.SquadMakerColumn != null &&
                   reference.SquadSettings != null &&
                   reference.SquadComposition != null &&
                   reference.SquadsColumn != null &&
                   reference.SavedSquadsColumn != null &&
                   reference.ChosenSquadsColumn != null;
        }

        private void ApplyViewportFill()
        {
            if (_canvasRect == null || !_referenceGeometryCaptured)
            {
                return;
            }

            if (_layoutReference == null)
            {
                CaptureSquadMakerLayoutReferenceGeometry();
            }

            bool authoredLayout = HasAuthoredLayoutReference();
            if (!authoredLayout)
            {
                if (!_structuralReferenceGeometryCaptured)
                {
                    CaptureStructuralChildReferenceGeometry();
                }

                RestoreStructuralChildReferenceGeometry();
                Canvas.ForceUpdateCanvases();
            }

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

            if (authoredLayout)
            {
                ApplySquadMakerLayoutContract();
            }
            else
            {
                RepairNestedStructuralLayouts(_canvasRect, _canvasRect, 0);
            }

            Canvas.ForceUpdateCanvases();
        }

        private bool ApplySquadMakerLayoutContract()
        {
            if (!HasAuthoredLayoutReference())
            {
                return false;
            }

            SquadMakerLayoutReferenceGeometry reference = _layoutReference;
            VerticalLayoutGroup mainPanelLayout = reference.MainPanel.GetComponent<VerticalLayoutGroup>();
            HorizontalLayoutGroup mainContainerLayout = reference.MainContainer.GetComponent<HorizontalLayoutGroup>();
            VerticalLayoutGroup squadMakerLayout = reference.SquadMakerColumn.GetComponent<VerticalLayoutGroup>();
            HorizontalLayoutGroup squadsLayout = reference.SquadsColumn.GetComponent<HorizontalLayoutGroup>();
            if (mainPanelLayout == null || mainContainerLayout == null ||
                squadMakerLayout == null || squadsLayout == null)
            {
                WarnMissingAuthoredHierarchy();
                return false;
            }

            // MainPanel.prefab is shared and is authored with decorative 5px padding/10px spacing.
            // Squad Maker is a viewport tiling surface, so its specialized owner must remove those
            // inherited gutters before the native LayoutGroups calculate body/footer/column sizes.
            NormalizeViewportLayout(mainPanelLayout);
            NormalizeViewportLayout(mainContainerLayout);
            NormalizeViewportLayout(squadMakerLayout);
            NormalizeViewportLayout(squadsLayout);

            // MainPanel owns width and height. The body is flexible; the footer keeps its authored
            // height. At the 768 reference height the authored 718+51 overlap resolves to a one-pixel
            // body reduction instead of leaving an uncovered strip.
            mainPanelLayout.childControlWidth = true;
            mainPanelLayout.childControlHeight = true;
            mainPanelLayout.childForceExpandWidth = true;
            mainPanelLayout.childForceExpandHeight = false;

            ConfigureLayoutElement(
                reference.MainContainer,
                -1f,
                -1f,
                1f,
                1f,
                reference.MainContainerSize.y,
                1f);
            ConfigureLayoutElement(
                reference.Footer,
                -1f,
                -1f,
                1f,
                reference.FooterSize.y,
                reference.FooterSize.y,
                0f);

            // Main Container owns all three columns. Left and right preserve their authored widths;
            // the center work area is the only flexible horizontal region.
            mainContainerLayout.childControlWidth = true;
            mainContainerLayout.childControlHeight = true;
            mainContainerLayout.childForceExpandWidth = false;
            mainContainerLayout.childForceExpandHeight = true;

            ConfigureFixedWidthFlexibleHeight(
                reference.ShipSelectorColumn,
                reference.ShipSelectorColumnSize.x,
                0f);
            ConfigureFixedWidthFlexibleHeight(
                reference.SquadMakerColumn,
                reference.SquadMakerColumnSize.x,
                1f);
            ConfigureFixedWidthFlexibleHeight(
                reference.SquadsColumn,
                reference.SquadsColumnSize.x,
                0f);

            // The center column is another structural layout owner. Its 298-high settings/presets
            // region stays at the authored height while filling the live center width. The composition
            // work region fills that same live width and absorbs any remaining vertical surplus. With
            // childControl* left false, Unity allocates force-expand space without resizing the actual
            // panels, which exposes the orange parent between/below them on tall and wide displays.
            squadMakerLayout.childControlWidth = true;
            squadMakerLayout.childControlHeight = true;
            squadMakerLayout.childForceExpandWidth = true;
            squadMakerLayout.childForceExpandHeight = false;

            ConfigureLayoutElement(
                reference.SquadSettings,
                -1f,
                -1f,
                1f,
                reference.SquadSettingsSize.y,
                reference.SquadSettingsSize.y,
                0f);
            ConfigureLayoutElement(
                reference.SquadComposition,
                -1f,
                -1f,
                1f,
                1f,
                reference.SquadCompositionSize.y,
                1f);

            // Squads Column is itself a two-column native layout. Its children exactly tile the
            // authored 484-wide region and inherit the body's live height.
            squadsLayout.childControlWidth = true;
            squadsLayout.childControlHeight = true;
            squadsLayout.childForceExpandWidth = false;
            squadsLayout.childForceExpandHeight = true;

            ConfigureFixedWidthFlexibleHeight(
                reference.SavedSquadsColumn,
                reference.SavedSquadsColumnSize.x,
                0f);
            ConfigureFixedWidthFlexibleHeight(
                reference.ChosenSquadsColumn,
                reference.ChosenSquadsColumnSize.x,
                0f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(reference.MainPanel);
            LayoutRebuilder.ForceRebuildLayoutImmediate(reference.MainContainer);
            LayoutRebuilder.ForceRebuildLayoutImmediate(reference.SquadMakerColumn);
            LayoutRebuilder.ForceRebuildLayoutImmediate(reference.SquadsColumn);

            // SquadMaker.ToggleLevelOptions/ToggleLevelDetails deliberately owns the reference
            // height of the chosen-squads ScrollView (663/415/278 depending semantic state). Keep
            // that stateful base intact and add only the height that exists beyond the authored
            // 718-high Chosen Squads Column. If SquadMaker writes a new semantic height later, the
            // next repair recognizes it as the new base instead of fighting the scene controller.
            ApplyChosenSquadScrollSurplus(reference);
            LayoutRebuilder.ForceRebuildLayoutImmediate(reference.ChosenSquadsColumn);
            return true;
        }

        private void ApplyChosenSquadScrollSurplus(SquadMakerLayoutReferenceGeometry reference)
        {
            if (reference == null || reference.ChosenSquadScroll == null ||
                reference.ChosenSquadsColumn == null)
            {
                return;
            }

            float currentHeight = Mathf.Abs(reference.ChosenSquadScroll.rect.height);
            bool semanticHeightChanged = _chosenSquadScrollReferenceHeight < 0f ||
                                         _lastAppliedChosenSquadScrollHeight < 0f ||
                                         Mathf.Abs(currentHeight - _lastAppliedChosenSquadScrollHeight) >
                                         ChosenScrollHeightTolerance;
            if (semanticHeightChanged)
            {
                _chosenSquadScrollReferenceHeight = currentHeight;
            }

            float targetHeight = CalculateSurplusAbsorbingHeight(
                _chosenSquadScrollReferenceHeight,
                reference.ChosenSquadsColumnSize.y,
                Mathf.Abs(reference.ChosenSquadsColumn.rect.height));

            if (Mathf.Abs(currentHeight - targetHeight) > ChosenScrollHeightTolerance)
            {
                reference.ChosenSquadScroll.SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Vertical,
                    targetHeight);
            }

            _lastAppliedChosenSquadScrollHeight = targetHeight;
        }

        internal static float CalculateSurplusAbsorbingHeight(
            float semanticBaseHeight,
            float authoredOwnerHeight,
            float liveOwnerHeight)
        {
            if (semanticBaseHeight <= 0f)
            {
                return semanticBaseHeight;
            }

            float surplus = Mathf.Max(0f, liveOwnerHeight - Mathf.Max(0f, authoredOwnerHeight));
            return semanticBaseHeight + surplus;
        }

        private static void NormalizeViewportLayout(HorizontalOrVerticalLayoutGroup layout)
        {
            if (layout == null)
            {
                return;
            }

            RectOffset padding = layout.padding;
            if (padding != null)
            {
                padding.left = 0;
                padding.right = 0;
                padding.top = 0;
                padding.bottom = 0;
            }

            layout.spacing = 0f;
            layout.childAlignment = TextAnchor.UpperLeft;
        }

        private static void ConfigureFixedWidthFlexibleHeight(
            RectTransform rect,
            float authoredWidth,
            float flexibleWidth)
        {
            ConfigureLayoutElement(
                rect,
                authoredWidth,
                authoredWidth,
                flexibleWidth,
                -1f,
                -1f,
                1f);
        }

        private static void ConfigureLayoutElement(
            RectTransform rect,
            float minWidth,
            float preferredWidth,
            float flexibleWidth,
            float minHeight,
            float preferredHeight,
            float flexibleHeight)
        {
            if (rect == null)
            {
                return;
            }

            LayoutElement element = rect.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = rect.gameObject.AddComponent<LayoutElement>();
            }

            element.ignoreLayout = false;
            element.minWidth = minWidth;
            element.preferredWidth = preferredWidth;
            element.flexibleWidth = flexibleWidth;
            element.minHeight = minHeight;
            element.preferredHeight = preferredHeight;
            element.flexibleHeight = flexibleHeight;
            element.layoutPriority = 1;
        }

        private static RectTransform FindAncestorByName(RectTransform start, string name)
        {
            Transform current = start;
            while (current != null)
            {
                RectTransform rect = current as RectTransform;
                if (rect != null && rect.name == name)
                {
                    return rect;
                }

                current = current.parent;
            }

            return null;
        }

        private static RectTransform FindDirectChildByName(RectTransform owner, string name)
        {
            if (owner == null)
            {
                return null;
            }

            for (int i = 0; i < owner.childCount; i++)
            {
                RectTransform child = owner.GetChild(i) as RectTransform;
                if (child != null && child.name == name)
                {
                    return child;
                }
            }

            return null;
        }

        private static RectTransform FindDirectChildAncestor(RectTransform descendant, RectTransform owner)
        {
            if (descendant == null || owner == null)
            {
                return null;
            }

            RectTransform current = descendant;
            while (current != null && current.parent != owner)
            {
                current = current.parent as RectTransform;
            }

            return current != null && current.parent == owner ? current : null;
        }

        private static RectTransform FindOwnedChild(RectTransform owner, string name)
        {
            RectTransform direct = FindDirectChildByName(owner, name);
            return direct != null ? direct : FindDescendantByName(owner, name);
        }

        private static RectTransform FindDescendantByName(RectTransform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            if (root.name == name)
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                RectTransform child = root.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                RectTransform found = FindDescendantByName(child, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool SetRectSize(RectTransform rect, Vector2 targetSize)
        {
            if (rect == null)
            {
                return false;
            }

            Vector2 currentSize = rect.rect.size;
            bool changed = !Approximately(currentSize, targetSize);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetSize.x);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetSize.y);
            return changed;
        }

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
