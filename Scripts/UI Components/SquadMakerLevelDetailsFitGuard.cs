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
    /// outer responsive geometry. This guard preserves the authored level-details/supply geometry,
    /// applies only the live column's vertical delta to the flexible details row, and restores the
    /// fixed Supply Capacity row itself if another layout pass has squeezed it into a clipped strip.
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
        private float _referenceChosenColumnHeight = -1f;
        private float _referenceDetailsHeight = -1f;
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

            // The previous repair only reserved enough budget for Supply Capacity but left the
            // actual row at its already-collapsed live height. Restore that fixed row first so its
            // text has a real RectTransform to render into.
            float protectedSupplyHeight = CalculateProtectedRowHeight(
                _referenceSupplyHeight,
                CalculatePreferredTextHeight(_supplyText, _supplyRow));
            SetHeightIfNeeded(_supplyRow, protectedSupplyHeight);

            float minimumDetailsHeight = CalculateMinimumDetailsHeight();
            float targetDetailsHeight = CalculateResponsiveDetailsHeight(
                _referenceDetailsHeight,
                _referenceChosenColumnHeight,
                ownerHeight,
                minimumDetailsHeight);
            SetHeightIfNeeded(_detailsRow, targetDetailsHeight);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_detailsRow);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);

            // Use the rendered supply bounds as the final authority. If a legacy row or layout
            // relationship still pushes any part of Supply Capacity below the chosen column, move
            // it back into view by giving up only that amount of flexible details height.
            float bottomOverflow = CalculateSupplyBottomOverflow(_chosenColumn, _supplyRow);
            if (bottomOverflow > SizeTolerance)
            {
                float correctedHeight = Mathf.Max(
                    minimumDetailsHeight,
                    targetDetailsHeight - bottomOverflow);
                if (SetHeightIfNeeded(_detailsRow, correctedHeight))
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_detailsRow);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);
                }
            }
        }

        private float CalculateMinimumDetailsHeight()
        {
            float preferredHeight = CalculatePreferredTextHeight(_levelDetailsText, _detailsRow);
            return preferredHeight > 0f ? preferredHeight + TextSafetyPadding : 0f;
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

        internal static float CalculateResponsiveDetailsHeight(
            float referenceDetailsHeight,
            float referenceOwnerHeight,
            float liveOwnerHeight,
            float minimumDetailsHeight)
        {
            float minimum = Mathf.Max(0f, minimumDetailsHeight);
            if (referenceDetailsHeight <= 0f || referenceOwnerHeight <= 0f)
            {
                return minimum;
            }

            float liveDelta = liveOwnerHeight - referenceOwnerHeight;
            return Mathf.Max(minimum, referenceDetailsHeight + liveDelta);
        }

        private static float CalculateSupplyBottomOverflow(
            RectTransform owner,
            RectTransform supplyRow)
        {
            if (owner == null || supplyRow == null)
            {
                return 0f;
            }

            Bounds renderedBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                owner,
                supplyRow);
            return CalculateBottomOverflow(owner.rect.yMin, renderedBounds.min.y);
        }

        internal static float CalculateBottomOverflow(float ownerBottom, float renderedBottom)
        {
            return Mathf.Max(0f, ownerBottom - renderedBottom);
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
