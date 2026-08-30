using Assets.Scripts.Scenes;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the fixed level-summary rows in the Squad Maker's Chosen Squads column visible while
    /// letting the active level-details row own the remaining vertical space.
    ///
    /// SquadMaker still owns the semantic chosen-list height (normal/options/level-details states),
    /// and SquadMakerResponsiveLayoutGuard still owns the outer responsive geometry. This guard only
    /// sizes the active level-details row to the height left after the other structural rows, so tall
    /// viewports place their surplus in the details panel while shorter viewports preserve the text's
    /// minimum readable height. The Supply Capacity row is measured from its text as well as its live
    /// RectTransform so a row that has already been squeezed by layout is not mistaken for its desired
    /// height and left as a thin clipped strip at the bottom of the column.
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

            _squadMaker = squadMaker;
            ResolveOwnedRows();
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

        private void ApplyFit()
        {
            if (_chosenColumn == null || _detailsRow == null ||
                !_detailsRow.gameObject.activeInHierarchy)
            {
                return;
            }

            float ownerHeight = Mathf.Abs(_chosenColumn.rect.height);
            if (ownerHeight <= SizeTolerance)
            {
                return;
            }

            float minimumSupplyHeight = CalculateMinimumSupplyHeight();
            float fixedLayoutHeight = CalculateOtherActiveRowHeight(
                _chosenColumn,
                _detailsRow,
                _supplyRow,
                minimumSupplyHeight);
            float minimumDetailsHeight = CalculateMinimumDetailsHeight();
            float targetHeight = CalculateFittingDetailsHeight(
                ownerHeight,
                fixedLayoutHeight,
                minimumDetailsHeight);

            if (Mathf.Abs(Mathf.Abs(_detailsRow.rect.height) - targetHeight) <= SizeTolerance)
            {
                return;
            }

            _detailsRow.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetHeight);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_detailsRow);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);
        }

        private float CalculateMinimumDetailsHeight()
        {
            if (_levelDetailsText == null || string.IsNullOrWhiteSpace(_levelDetailsText.text))
            {
                return 0f;
            }

            RectTransform textRect = _levelDetailsText.rectTransform;
            float width = textRect != null ? Mathf.Abs(textRect.rect.width) : 0f;
            if (width <= SizeTolerance && _detailsRow != null)
            {
                width = Mathf.Abs(_detailsRow.rect.width);
            }
            if (width <= SizeTolerance)
            {
                return 0f;
            }

            float preferredHeight = _levelDetailsText.GetPreferredValues(
                _levelDetailsText.text,
                width,
                0f).y;
            return Mathf.Max(0f, preferredHeight + TextSafetyPadding);
        }

        private float CalculateMinimumSupplyHeight()
        {
            if (_supplyRow == null)
            {
                return 0f;
            }

            float currentHeight = Mathf.Abs(_supplyRow.rect.height);
            if (_supplyText == null || string.IsNullOrWhiteSpace(_supplyText.text))
            {
                return currentHeight;
            }

            RectTransform textRect = _supplyText.rectTransform;
            float width = textRect != null ? Mathf.Abs(textRect.rect.width) : 0f;
            if (width <= SizeTolerance)
            {
                width = Mathf.Abs(_supplyRow.rect.width);
            }
            if (width <= SizeTolerance)
            {
                return currentHeight;
            }

            float preferredTextHeight = _supplyText.GetPreferredValues(
                _supplyText.text,
                width,
                0f).y;
            return CalculateProtectedRowHeight(currentHeight, preferredTextHeight);
        }

        internal static float CalculateProtectedRowHeight(
            float currentHeight,
            float preferredTextHeight)
        {
            float protectedTextHeight = preferredTextHeight > 0f
                ? preferredTextHeight + TextSafetyPadding
                : 0f;
            return Mathf.Max(0f, Mathf.Max(currentHeight, protectedTextHeight));
        }

        internal static float CalculateOtherActiveRowHeight(
            RectTransform column,
            RectTransform excludedRow,
            RectTransform protectedRow,
            float protectedRowMinimumHeight)
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
                if (child != excludedRow)
                {
                    float childHeight = Mathf.Abs(child.rect.height);
                    if (child == protectedRow)
                    {
                        childHeight = Mathf.Max(childHeight, protectedRowMinimumHeight);
                    }
                    height += childHeight;
                }
            }

            if (layout != null && layoutChildCount > 1)
            {
                height += layout.spacing * (layoutChildCount - 1);
            }

            return height;
        }

        internal static float CalculateFittingDetailsHeight(
            float ownerHeight,
            float fixedLayoutHeight,
            float minimumDetailsHeight)
        {
            float minimum = Mathf.Max(0f, minimumDetailsHeight);
            float available = Mathf.Max(0f, ownerHeight - Mathf.Max(0f, fixedLayoutHeight));
            return Mathf.Max(minimum, available);
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
