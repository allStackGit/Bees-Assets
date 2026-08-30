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
    /// outer responsive geometry. This guard restores the fixed Supply Capacity row, measures every
    /// other active structural row in the live column, and gives the flexible level-details row only
    /// the height that actually remains. That prevents a tall neighboring row from pushing Supply
    /// Capacity, START, or TEST below the visible column.
    /// </summary>
    [DefaultExecutionOrder(-600)]
    public sealed class SquadMakerLevelDetailsFitGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string ChosenSquadsColumnName = "Chosen Squads Column";
        private const float RepairInterval = 0.20f;
        private const float SizeTolerance = 0.01f;
        private const float TextSafetyPadding = 8f;

        private SquadMaker _squadMaker;
        private RectTransform _chosenColumn;
        private RectTransform _detailsRow;
        private RectTransform _supplyRow;
        private TMP_Text _levelDetailsText;
        private TMP_Text _supplyText;
        private float _nextRepairTime;
        private float _referenceSupplyHeight = -1f;
        private bool _referenceGeometryCaptured;

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
            if (_chosenColumn == null || _detailsRow == null || _supplyRow == null)
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
            _detailsRow = FindDirectChildAncestor(details, _chosenColumn);
            _levelDetailsText = _squadMaker != null ? _squadMaker.LevelDetails : null;

            RectTransform supply = _squadMaker != null && _squadMaker.ChosenSquadsSupplyCapacityLabel != null
                ? _squadMaker.ChosenSquadsSupplyCapacityLabel.transform as RectTransform
                : null;
            _supplyRow = FindDirectChildAncestor(supply, _chosenColumn);
            _supplyText = _squadMaker != null && _squadMaker.ChosenSquadsSupplyCapacityLabel != null
                ? _squadMaker.ChosenSquadsSupplyCapacityLabel.GetComponentInChildren<TMP_Text>()
                : null;
        }

        private void CaptureReferenceGeometry()
        {
            if (_referenceGeometryCaptured || _chosenColumn == null ||
                _detailsRow == null || _supplyRow == null)
            {
                return;
            }

            float supplyHeight = Mathf.Abs(_supplyRow.rect.height);
            if (Mathf.Abs(_chosenColumn.rect.height) <= SizeTolerance ||
                Mathf.Abs(_detailsRow.rect.height) <= SizeTolerance ||
                supplyHeight <= SizeTolerance)
            {
                return;
            }

            _referenceSupplyHeight = supplyHeight;
            _referenceGeometryCaptured = true;
        }

        private void ApplyFit()
        {
            if (!_referenceGeometryCaptured || _chosenColumn == null || _detailsRow == null ||
                _supplyRow == null || !_detailsRow.gameObject.activeInHierarchy)
            {
                return;
            }

            float ownerHeight = Mathf.Abs(_chosenColumn.rect.height);
            if (ownerHeight <= SizeTolerance)
            {
                return;
            }

            // Supply Capacity is a fixed summary row. Restore its real RectTransform before measuring
            // the column so an already-squeezed live rect cannot hide the amount of space it needs.
            float protectedSupplyHeight = CalculateProtectedRowHeight(
                _referenceSupplyHeight,
                CalculatePreferredTextHeight(_supplyText, _supplyRow));
            SetHeightIfNeeded(_supplyRow, protectedSupplyHeight);

            // The level report is the flexible row in this state. Measure the other live layout rows
            // every pass rather than assuming their authored heights still add up to the reference
            // column height. This is the missing protection when a neighboring row becomes taller.
            float fixedLayoutHeight = CalculateOtherActiveRowHeight(
                _chosenColumn,
                _detailsRow,
                _supplyRow,
                protectedSupplyHeight);
            float targetDetailsHeight = CalculateFittingDetailsHeight(ownerHeight, fixedLayoutHeight);
            SetHeightIfNeeded(_detailsRow, targetDetailsHeight);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_detailsRow);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);
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
            float ownerHeight,
            float fixedLayoutHeight)
        {
            return Mathf.Max(0f, ownerHeight - Mathf.Max(0f, fixedLayoutHeight));
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
