using Assets.Scripts.Scenes;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Owns the final presentation of the Chosen Squads Supply Capacity row.
    ///
    /// The red over-capacity background is the structural Supply Capacity row itself, not the TMP
    /// label. The row sits immediately above the Squad Maker footer, while START/TEST hover text is
    /// later reparented into a root-canvas overlay. Final presentation therefore has two jobs after
    /// the structural/interaction guards have run: keep the complete row above the real footer
    /// boundary, and keep hover descriptions from covering that row.
    /// </summary>
    [DefaultExecutionOrder(-575)]
    public sealed class SquadMakerSupplyCapacityPresentationGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string ChosenSquadsColumnName = "Chosen Squads Column";
        private const string FooterName = "Footer";
        private const float FooterSafetyGap = 6f;
        private const float HoverSafetyGap = 8f;
        private const float OverlayMargin = 8f;
        private const float SizeTolerance = 0.01f;
        private const int ClearanceRepairPasses = 2;

        private SquadMaker _squadMaker;
        private RectTransform _row;
        private RectTransform _label;
        private TMP_Text _text;
        private RectTransform _chosenColumn;
        private RectTransform _detailsRow;
        private RectTransform _footer;
        private RectTransform _startButton;
        private RectTransform _testButton;
        private RectTransform _rootCanvas;

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

                SquadMakerSupplyCapacityPresentationGuard guard =
                    squadMaker.GetComponent<SquadMakerSupplyCapacityPresentationGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerSupplyCapacityPresentationGuard>();
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
            ResolvePresentation();
            ApplyPresentation();
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            if (_row == null || _label == null || _text == null ||
                _chosenColumn == null || _rootCanvas == null)
            {
                ResolvePresentation();
            }

            // Run every frame after the responsive, interaction, level-fit, and text-size guards.
            // Those owners are allowed to rebuild/reposition their geometry in LateUpdate; this is
            // the final presentation pass that prevents their valid outputs from visually occluding
            // the fixed Supply Capacity row.
            ApplyPresentation();
        }

        private void ResolvePresentation()
        {
            GameObject labelObject = _squadMaker != null
                ? _squadMaker.ChosenSquadsSupplyCapacityLabel
                : null;
            _label = labelObject != null ? labelObject.transform as RectTransform : null;
            _row = _label != null ? _label.parent as RectTransform : null;
            _text = labelObject != null ? labelObject.GetComponentInChildren<TMP_Text>(true) : null;

            _chosenColumn = FindAncestorByName(_row, ChosenSquadsColumnName);
            RectTransform details = _squadMaker != null && _squadMaker.LevelDetailsContainer != null
                ? _squadMaker.LevelDetailsContainer.transform as RectTransform
                : null;
            _detailsRow = FindDirectChildAncestor(details, _chosenColumn);

            _startButton = _squadMaker != null && _squadMaker.StartButton != null
                ? _squadMaker.StartButton.transform as RectTransform
                : null;
            _testButton = _squadMaker != null && _squadMaker.TestButton != null
                ? _squadMaker.TestButton.transform as RectTransform
                : null;
            _footer = FindAncestorByName(_startButton, FooterName);
            if (_footer == null)
            {
                _footer = FindAncestorByName(_testButton, FooterName);
            }

            Canvas canvas = _row != null ? _row.GetComponentInParent<Canvas>() : null;
            Canvas rootCanvas = canvas != null ? canvas.rootCanvas : null;
            _rootCanvas = rootCanvas != null ? rootCanvas.transform as RectTransform : null;
        }

        private void ApplyPresentation()
        {
            CenterPresentation(_row, _label, _text);
            RepairSupplyRowFooterClearance();
            KeepHoverDescriptionClear(_squadMaker != null ? _squadMaker.StartText : null);
            KeepHoverDescriptionClear(_squadMaker != null ? _squadMaker.TestText : null);
        }

        private void RepairSupplyRowFooterClearance()
        {
            if (_row == null || _chosenColumn == null || _detailsRow == null ||
                !_row.gameObject.activeInHierarchy || !_detailsRow.gameObject.activeInHierarchy)
            {
                return;
            }

            for (int pass = 0; pass < ClearanceRepairPasses; pass++)
            {
                Rect rowRect = GetRectInLocalSpace(_row, _chosenColumn);
                Rect footerRect = GetRectInLocalSpace(_footer, _chosenColumn);
                Rect firstControlRect = GetRectInLocalSpace(_startButton, _chosenColumn);
                Rect secondControlRect = GetRectInLocalSpace(_testButton, _chosenColumn);

                float clearance = CalculateRequiredUpwardClearance(
                    rowRect,
                    _chosenColumn.rect,
                    footerRect,
                    IsActive(_footer),
                    firstControlRect,
                    IsActive(_startButton),
                    secondControlRect,
                    IsActive(_testButton),
                    FooterSafetyGap);
                if (clearance <= SizeTolerance)
                {
                    return;
                }

                float currentDetailsHeight = Mathf.Abs(_detailsRow.rect.height);
                float shrink = Mathf.Min(currentDetailsHeight, clearance);
                if (shrink <= SizeTolerance)
                {
                    return;
                }

                _detailsRow.SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Vertical,
                    currentDetailsHeight - shrink);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_detailsRow);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_chosenColumn);
            }
        }

        private void KeepHoverDescriptionClear(GameObject descriptionObject)
        {
            RectTransform description = descriptionObject != null
                ? descriptionObject.transform as RectTransform
                : null;
            RectTransform owner = description != null ? description.parent as RectTransform : null;
            if (description == null || owner == null || _row == null ||
                !description.gameObject.activeInHierarchy || !_row.gameObject.activeInHierarchy)
            {
                return;
            }

            // SquadMakerInteractionGuard reparents START/TEST descriptions to a full-canvas overlay
            // and positions them before this component runs. Only intervene when that final overlay
            // rectangle actually covers the Supply Capacity row.
            Rect descriptionRect = GetRectInLocalSpace(description, owner);
            Rect protectedRect = GetRectInLocalSpace(_row, owner);
            float upwardShift = CalculateHoverUpwardShift(
                descriptionRect,
                protectedRect,
                owner.rect,
                HoverSafetyGap,
                OverlayMargin);
            if (upwardShift > SizeTolerance)
            {
                description.anchoredPosition += new Vector2(0f, upwardShift);
            }
        }

        internal static void CenterPresentation(
            RectTransform row,
            RectTransform label,
            TMP_Text text)
        {
            if (row == null || label == null || text == null)
            {
                return;
            }

            StretchToParent(label);

            RectTransform textRect = text.rectTransform;
            if (textRect != null)
            {
                StretchToParent(textRect);
            }

            text.alignment = TextAlignmentOptions.Center;
            text.margin = Vector4.zero;
        }

        internal static float CalculateRequiredUpwardClearance(
            Rect rowRect,
            Rect ownerRect,
            Rect footerRect,
            bool footerActive,
            Rect firstControlRect,
            bool firstControlActive,
            Rect secondControlRect,
            bool secondControlActive,
            float gap = FooterSafetyGap)
        {
            float safeBottom = ownerRect.yMin;
            if (footerActive)
            {
                // The footer background itself is the occluding surface. Protecting only the tops
                // of START/TEST can still leave several pixels of the row hidden behind that surface.
                safeBottom = Mathf.Max(safeBottom, footerRect.yMax);
            }
            if (firstControlActive)
            {
                safeBottom = Mathf.Max(safeBottom, firstControlRect.yMax);
            }
            if (secondControlActive)
            {
                safeBottom = Mathf.Max(safeBottom, secondControlRect.yMax);
            }

            return Mathf.Max(0f, safeBottom + Mathf.Max(0f, gap) - rowRect.yMin);
        }

        internal static float CalculateHoverUpwardShift(
            Rect descriptionRect,
            Rect protectedRect,
            Rect overlayRect,
            float gap = HoverSafetyGap,
            float margin = OverlayMargin)
        {
            if (!Overlaps(descriptionRect, protectedRect))
            {
                return 0f;
            }

            float requestedShift = protectedRect.yMax + Mathf.Max(0f, gap) - descriptionRect.yMin;
            if (requestedShift <= 0f)
            {
                return 0f;
            }

            float maximumShift = overlayRect.yMax - Mathf.Max(0f, margin) - descriptionRect.yMax;
            return Mathf.Clamp(requestedShift, 0f, Mathf.Max(0f, maximumShift));
        }

        private static bool Overlaps(Rect first, Rect second)
        {
            return first.xMin < second.xMax && first.xMax > second.xMin &&
                   first.yMin < second.yMax && first.yMax > second.yMin;
        }

        private static bool IsActive(RectTransform rect)
        {
            return rect != null && rect.gameObject.activeInHierarchy;
        }

        private static Rect GetRectInLocalSpace(RectTransform rect, RectTransform owner)
        {
            if (rect == null || owner == null)
            {
                return default(Rect);
            }

            Vector3[] worldCorners = new Vector3[4];
            rect.GetWorldCorners(worldCorners);
            Vector3 bottomLeft = owner.InverseTransformPoint(worldCorners[0]);
            Vector3 topRight = owner.InverseTransformPoint(worldCorners[2]);
            return Rect.MinMaxRect(bottomLeft.x, bottomLeft.y, topRight.x, topRight.y);
        }

        private static void StretchToParent(RectTransform rect)
        {
            if (rect == null || !(rect.parent is RectTransform))
            {
                return;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;
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
