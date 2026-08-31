using Assets.Scripts.Scenes;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the campaign level summary readable in the Squad Maker's Chosen Squads column.
    ///
    /// SquadMaker owns the semantic chosen-list height and SquadMakerResponsiveLayoutGuard owns the
    /// outer responsive geometry. While level details are visible this guard remembers the list height
    /// selected when that state was entered, prevents the native VerticalLayoutGroup from distributing
    /// spare height back into the structural rows, fits the flexible report, and finally verifies the
    /// rendered Supply Capacity row against the real START/TEST footer boundary. This makes the final
    /// visible geometry authoritative instead of assuming that nominal row heights imply safe placement.
    /// </summary>
    [DefaultExecutionOrder(-600)]
    public sealed class SquadMakerLevelDetailsFitGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string ChosenSquadsColumnName = "Chosen Squads Column";
        private const float RepairInterval = 0.20f;
        private const float SizeTolerance = 0.01f;
        private const float TextSafetyPadding = 8f;
        private const float BottomControlSafetyGap = 0f;
        private const int BottomClearanceRepairPasses = 2;

        private SquadMaker _squadMaker;
        private RectTransform _chosenColumn;
        private RectTransform _chosenListRow;
        private RectTransform _detailsRow;
        private RectTransform _supplyRow;
        private RectTransform _startButtonRect;
        private RectTransform _testButtonRect;
        private VerticalLayoutGroup _chosenColumnLayout;
        private TMP_Text _supplyText;
        private float _nextRepairTime;
        private float _referenceChosenColumnHeight = -1f;
        private float _referenceDetailsHeight = -1f;
        private float _referenceSupplyHeight = -1f;
        private float _levelDetailsChosenListHeight = -1f;
        private bool _levelDetailsVisibilityKnown;
        private bool _levelDetailsWasVisible;
        private bool _referenceGeometryCaptured;
        private bool _chosenColumnLayoutReferenceCaptured;
        private bool _referenceChosenColumnForceExpandHeight;

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

                SquadMakerLevelDetailsFitGuard guard =
                    squadMaker.GetComponent<SquadMakerLevelDetailsFitGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerLevelDetailsFitGuard>();
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
            if (squadMaker == null)
            {
                return;
            }

            // AddComponent invokes Awake before the scene-loaded callback returns. Do not recapture
            // geometry from a later responsive result when that callback immediately initializes the
            // same component a second time.
            if (_squadMaker == squadMaker && _referenceGeometryCaptured)
            {
                _nextRepairTime = 0f;
                return;
            }

            _squadMaker = squadMaker;
            _levelDetailsChosenListHeight = -1f;
            _levelDetailsVisibilityKnown = false;
            _levelDetailsWasVisible = false;
            _chosenColumnLayoutReferenceCaptured = false;
            ResolveOwnedRows();
            CaptureReferenceGeometry();
            _nextRepairTime = 0f;
        }

        private void LateUpdate()
        {
            if (_squadMaker == null || Time.unscaledTime < _nextRepairTime)
            {
                return;
            }

            _nextRepairTime = Time.unscaledTime + RepairInterval;
            if (_chosenColumn == null || _chosenListRow == null ||
                _detailsRow == null || _supplyRow == null)
            {
                ResolveOwnedRows();
            }
            if (!_referenceGeometryCaptured)
            {
                CaptureReferenceGeometry();
            }

            ApplyFit();
        }

        private void ResolveOwnedRows()
        {
            RectTransform details = _squadMaker != null && _squadMaker.LevelDetailsContainer != null
                ? _squadMaker.LevelDetailsContainer.transform as RectTransform
                : null;
            _chosenColumn = FindAncestorByName(details, ChosenSquadsColumnName);
            _chosenColumnLayout = _chosenColumn != null
                ? _chosenColumn.GetComponent<VerticalLayoutGroup>()
                : null;
            _detailsRow = FindDirectChildAncestor(details, _chosenColumn);

            RectTransform chosenList = _squadMaker != null && _squadMaker.ChosenSquadList != null
                ? _squadMaker.ChosenSquadList.transform as RectTransform
                : null;
            _chosenListRow = FindDirectChildAncestor(chosenList, _chosenColumn);

            RectTransform supply = _squadMaker != null && _squadMaker.ChosenSquadsSupplyCapacityLabel != null
                ? _squadMaker.ChosenSquadsSupplyCapacityLabel.transform as RectTransform
                : null;
            _supplyRow = FindDirectChildAncestor(supply, _chosenColumn);
            _supplyText = _squadMaker != null && _squadMaker.ChosenSquadsSupplyCapacityLabel != null
                ? _squadMaker.ChosenSquadsSupplyCapacityLabel.GetComponentInChildren<TMP_Text>()
                : null;

            _startButtonRect = _squadMaker != null && _squadMaker.StartButton != null
                ? _squadMaker.StartButton.transform as RectTransform
                : null;
            _testButtonRect = _squadMaker != null && _squadMaker.TestButton != null
                ? _squadMaker.TestButton.transform as RectTransform
                : null;
        }

        private void CaptureReferenceGeometry()
        {
            CaptureChosenColumnLayoutReference();

            if (_referenceGeometryCaptured || _chosenColumn == null ||
                _detailsRow == null || _supplyRow == null)
            {
                return;
            }

            float ownerHeight = Mathf.Abs(_chosenColumn.rect.height);
            float detailsHeight = Mathf.Abs(_detailsRow.rect.height);
            float supplyHeight = Mathf.Abs(_supplyRow.rect.height);
            if (ownerHeight <= SizeTolerance || detailsHeight <= SizeTolerance || supplyHeight <= SizeTolerance)
            {
                return;
            }

            _referenceChosenColumnHeight = ownerHeight;
            _referenceDetailsHeight = detailsHeight;
            _referenceSupplyHeight = supplyHeight;
            _referenceGeometryCaptured = true;
        }

        private void CaptureChosenColumnLayoutReference()
        {
            if (_chosenColumnLayoutReferenceCaptured)
            {
                return;
            }

            if (_chosenColumnLayout == null && _chosenColumn != null)
            {
                _chosenColumnLayout = _chosenColumn.GetComponent<VerticalLayoutGroup>();
            }
            if (_chosenColumnLayout == null)
            {
                return;
            }

            _referenceChosenColumnForceExpandHeight = _chosenColumnLayout.childForceExpandHeight;
            _chosenColumnLayoutReferenceCaptured = true;
        }

        private void ApplyFit()
        {
            if (!_referenceGeometryCaptured || _chosenColumn == null ||
                _detailsRow == null || _supplyRow == null)
            {
                return;
            }

            bool detailsVisible = _detailsRow.gameObject.activeInHierarchy;
            bool chosenListRestored = StabilizeLevelDetailsChosenListHeight(detailsVisible);
            bool columnLayoutChanged = StabilizeChosenColumnVerticalLayout(detailsVisible);
            if (!detailsVisible)
            {
                if (columnLayoutChanged)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);
                }
                return;
            }

            float ownerHeight = Mathf.Abs(_chosenColumn.rect.height);
            if (ownerHeight <= SizeTolerance)
            {
                return;
            }

            if (chosenListRestored || columnLayoutChanged)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);
            }

            // Supply Capacity is the selected-level budget shown at the bottom of the right-hand
            // Chosen Squads column. Restore its real RectTransform before measuring the column so an
            // already-squeezed live rect cannot hide the amount of space it needs.
            float protectedSupplyHeight = CalculateProtectedRowHeight(
                _referenceSupplyHeight,
                CalculatePreferredTextHeight(_supplyText, _supplyRow));
            SetHeightIfNeeded(_supplyRow, protectedSupplyHeight);

            // The level report is the one flexible structural row in this state. Once the semantic
            // chosen-list height and all fixed neighbors are accounted for, give the report every
            // remaining live pixel. Preserving arbitrary authored slack here leaves Supply Capacity
            // floating above the body/footer boundary and can reintroduce clipping when neighbors grow.
            float fixedLayoutHeight = CalculateOtherActiveRowHeight(
                _chosenColumn,
                _detailsRow,
                _supplyRow,
                protectedSupplyHeight);
            float targetDetailsHeight = CalculateFittingDetailsHeight(
                _referenceDetailsHeight,
                _referenceChosenColumnHeight,
                ownerHeight,
                fixedLayoutHeight);
            SetHeightIfNeeded(_detailsRow, targetDetailsHeight);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_detailsRow);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);

            // Nominal row arithmetic is not sufficient here because START/TEST live in the footer,
            // outside this VerticalLayoutGroup. Verify the final rendered geometry and move the
            // flexible report upward if the Supply Capacity row would enter that footer region.
            EnforceRenderedBottomClearance();
        }

        private bool StabilizeChosenColumnVerticalLayout(bool detailsVisible)
        {
            CaptureChosenColumnLayoutReference();
            if (_chosenColumnLayout == null || !_chosenColumnLayoutReferenceCaptured)
            {
                return false;
            }

            // Panel.prefab authors this as true. That is useful for generic panels but wrong while
            // this column has an explicit semantic list height plus a single flexible report: Unity
            // otherwise distributes spare height back into every cell after this guard sizes them.
            bool targetForceExpandHeight = detailsVisible
                ? false
                : _referenceChosenColumnForceExpandHeight;
            if (_chosenColumnLayout.childForceExpandHeight == targetForceExpandHeight)
            {
                return false;
            }

            _chosenColumnLayout.childForceExpandHeight = targetForceExpandHeight;
            return true;
        }

        private void EnforceRenderedBottomClearance()
        {
            for (int pass = 0; pass < BottomClearanceRepairPasses; pass++)
            {
                float requiredClearance = CalculateRequiredBottomClearance(
                    _chosenColumn,
                    _supplyRow,
                    _startButtonRect,
                    _testButtonRect);
                if (requiredClearance <= SizeTolerance)
                {
                    return;
                }

                float currentDetailsHeight = Mathf.Abs(_detailsRow.rect.height);
                float targetDetailsHeight = Mathf.Max(0f, currentDetailsHeight - requiredClearance);
                if (!SetHeightIfNeeded(_detailsRow, targetDetailsHeight))
                {
                    return;
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(_detailsRow);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);
            }
        }

        private bool StabilizeLevelDetailsChosenListHeight(bool detailsVisible)
        {
            bool enteringDetails = detailsVisible &&
                                   (!_levelDetailsVisibilityKnown || !_levelDetailsWasVisible);
            _levelDetailsVisibilityKnown = true;
            _levelDetailsWasVisible = detailsVisible;

            if (!detailsVisible || _chosenListRow == null)
            {
                return false;
            }

            float currentHeight = Mathf.Abs(_chosenListRow.rect.height);
            _levelDetailsChosenListHeight = CalculateStableLevelDetailsListHeight(
                _levelDetailsChosenListHeight,
                currentHeight,
                enteringDetails);

            return _levelDetailsChosenListHeight > SizeTolerance &&
                   SetHeightIfNeeded(_chosenListRow, _levelDetailsChosenListHeight);
        }

        internal static float CalculateStableLevelDetailsListHeight(
            float capturedHeight,
            float currentHeight,
            bool enteringDetails)
        {
            if (currentHeight <= SizeTolerance)
            {
                return Mathf.Max(0f, capturedHeight);
            }

            if (enteringDetails || capturedHeight <= SizeTolerance)
            {
                return currentHeight;
            }

            // Once the controller's level-details height has been observed, later layout/responsive
            // mutations are presentation changes, not semantic state changes. Do not promote them to
            // the new base while the same level-details state remains active.
            return capturedHeight;
        }

        private static float CalculatePreferredTextHeight(TMP_Text text, RectTransform fallbackRow)
        {
            if (text == null || string.IsNullOrWhiteSpace(text.text))
            {
                return 0f;
            }

            RectTransform textRect = text.rectTransform;
            float width = textRect != null ? Mathf.Abs(textRect.rect.width) : 0f;
            if (width <= SizeTolerance && fallbackRow != null)
            {
                width = Mathf.Abs(fallbackRow.rect.width);
            }
            if (width <= SizeTolerance)
            {
                return 0f;
            }

            return Mathf.Max(0f, text.GetPreferredValues(text.text, width, 0f).y);
        }

        internal static float CalculateProtectedRowHeight(
            float referenceHeight,
            float preferredTextHeight)
        {
            float protectedTextHeight = preferredTextHeight > 0f
                ? preferredTextHeight + TextSafetyPadding
                : 0f;
            return Mathf.Max(0f, Mathf.Max(referenceHeight, protectedTextHeight));
        }

        internal static float CalculateOtherActiveRowHeight(
            RectTransform column,
            RectTransform excludedRow,
            RectTransform protectedRow,
            float protectedRowHeight)
        {
            if (column == null)
            {
                return 0f;
            }

            VerticalLayoutGroup layout = column.GetComponent<VerticalLayoutGroup>();
            float height = layout != null
                ? layout.padding.top + layout.padding.bottom
                : 0f;
            int layoutChildCount = 0;

            for (int index = 0; index < column.childCount; index++)
            {
                RectTransform child = column.GetChild(index) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                LayoutElement element = child.GetComponent<LayoutElement>();
                if (element != null && element.ignoreLayout)
                {
                    continue;
                }

                layoutChildCount++;
                if (child == excludedRow)
                {
                    continue;
                }

                float childHeight = Mathf.Abs(child.rect.height);
                if (child == protectedRow)
                {
                    childHeight = Mathf.Max(childHeight, protectedRowHeight);
                }
                height += childHeight;
            }

            if (layout != null && layoutChildCount > 1)
            {
                height += layout.spacing * (layoutChildCount - 1);
            }

            return Mathf.Max(0f, height);
        }

        internal static float CalculateFittingDetailsHeight(
            float referenceDetailsHeight,
            float referenceOwnerHeight,
            float liveOwnerHeight,
            float fixedLayoutHeight)
        {
            // The reference arguments remain part of this helper's internal test seam, but level-details
            // mode deliberately does not preserve reference slack. The report owns the entire remainder.
            return Mathf.Max(0f, liveOwnerHeight - Mathf.Max(0f, fixedLayoutHeight));
        }

        internal static float CalculateRequiredBottomClearance(
            RectTransform owner,
            RectTransform protectedRow,
            RectTransform firstBottomControl,
            RectTransform secondBottomControl)
        {
            if (owner == null || protectedRow == null || !protectedRow.gameObject.activeInHierarchy)
            {
                return 0f;
            }

            float safeBottom = owner.rect.yMin;
            VerticalLayoutGroup layout = owner.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                safeBottom += layout.padding.bottom;
            }

            safeBottom = Mathf.Max(
                safeBottom,
                CalculateActiveControlTop(owner, firstBottomControl) + BottomControlSafetyGap);
            safeBottom = Mathf.Max(
                safeBottom,
                CalculateActiveControlTop(owner, secondBottomControl) + BottomControlSafetyGap);

            Bounds protectedBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                owner,
                protectedRow);
            return Mathf.Max(0f, safeBottom - protectedBounds.min.y);
        }

        private static float CalculateActiveControlTop(RectTransform owner, RectTransform control)
        {
            if (owner == null || control == null || !control.gameObject.activeInHierarchy)
            {
                return float.NegativeInfinity;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(owner, control);
            return bounds.max.y;
        }

        private static bool SetHeightIfNeeded(RectTransform rect, float targetHeight)
        {
            if (rect == null || targetHeight < 0f)
            {
                return false;
            }

            if (Mathf.Abs(Mathf.Abs(rect.rect.height) - targetHeight) <= SizeTolerance)
            {
                return false;
            }

            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetHeight);
            return true;
        }

        private static RectTransform FindAncestorByName(RectTransform start, string name)
        {
            Transform current = start;
            while (current != null)
            {
                if (current.name == name)
                {
                    return current as RectTransform;
                }
                current = current.parent;
            }
            return null;
        }

        private static RectTransform FindDirectChildAncestor(RectTransform start, RectTransform owner)
        {
            if (start == null || owner == null)
            {
                return null;
            }

            RectTransform current = start;
            while (current != null && current.parent != owner)
            {
                current = current.parent as RectTransform;
            }
            return current != null && current.parent == owner ? current : null;
        }
    }
}
