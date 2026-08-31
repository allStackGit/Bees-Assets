using Assets.Scripts.Scenes;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Removes only the unused top portion of the Squad Maker footer while preserving the authored
    /// screen position of every footer branch.
    ///
    /// The serialized footer is 51 units high, but its controls occupy a smaller bottom-relative
    /// envelope. Shrinking the footer without preserving those bottom-relative branch positions moves
    /// top-anchored branches downward and clips START/TEST. Moving START/TEST upward instead closes the
    /// visible strip but makes the Supply Capacity warning touch the buttons. This guard measures the
    /// complete authored Button envelope, trims the footer to that measured height, and then restores
    /// each direct footer branch to its captured distance from the footer bottom. The body therefore
    /// receives exactly the genuinely unused footer height while BACK/START/TEST/NEXT remain where the
    /// scene authored them.
    /// </summary>
    [DefaultExecutionOrder(-660)]
    public sealed class SquadMakerFooterActionAlignmentGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string FooterName = "Footer";
        private const float PositionTolerance = 0.01f;

        private sealed class FooterBranchReference
        {
            public RectTransform Rect;
            public float BottomOffset;
        }

        private SquadMaker _squadMaker;
        private RectTransform _footer;
        private RectTransform _mainPanel;
        private LayoutElement _footerLayoutElement;
        private readonly List<FooterBranchReference> _branchReferences =
            new List<FooterBranchReference>();
        private float _targetFooterHeight = -1f;
        private bool _referenceCaptured;

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

                SquadMakerFooterActionAlignmentGuard guard =
                    squadMaker.GetComponent<SquadMakerFooterActionAlignmentGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerFooterActionAlignmentGuard>();
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

            if (_squadMaker == squadMaker && _referenceCaptured && _footer != null)
            {
                return;
            }

            _squadMaker = squadMaker;
            ResolveOwnedGeometry();
            CaptureReferenceGeometry();
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            if (_footer == null || _mainPanel == null)
            {
                ResolveOwnedGeometry();
            }

            if (!_referenceCaptured)
            {
                CaptureReferenceGeometry();
            }

            ApplyMeasuredFooterFit();
        }

        private void ResolveOwnedGeometry()
        {
            RectTransform startButton = _squadMaker != null && _squadMaker.StartButton != null
                ? _squadMaker.StartButton.transform as RectTransform
                : null;
            RectTransform testButton = _squadMaker != null && _squadMaker.TestButton != null
                ? _squadMaker.TestButton.transform as RectTransform
                : null;
            RectTransform nextButton = _squadMaker != null && _squadMaker.NextButton != null
                ? _squadMaker.NextButton.transform as RectTransform
                : null;

            _footer = FindAncestorByName(startButton, FooterName);
            if (_footer == null)
            {
                _footer = FindAncestorByName(testButton, FooterName);
            }
            if (_footer == null)
            {
                _footer = FindAncestorByName(nextButton, FooterName);
            }

            _mainPanel = _footer != null ? _footer.parent as RectTransform : null;
            _footerLayoutElement = _footer != null ? _footer.GetComponent<LayoutElement>() : null;
        }

        private void CaptureReferenceGeometry()
        {
            if (_footer == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();

            float measuredHeight = CalculateFooterControlEnvelopeHeight(_footer);
            if (measuredHeight <= PositionTolerance)
            {
                return;
            }

            _targetFooterHeight = measuredHeight;
            _branchReferences.Clear();
            for (int index = 0; index < _footer.childCount; index++)
            {
                RectTransform branch = _footer.GetChild(index) as RectTransform;
                if (branch == null)
                {
                    continue;
                }

                _branchReferences.Add(new FooterBranchReference
                {
                    Rect = branch,
                    BottomOffset = CalculateBottomOffset(_footer, branch)
                });
            }

            _referenceCaptured = true;
        }

        private void ApplyMeasuredFooterFit()
        {
            if (!_referenceCaptured || _footer == null || _mainPanel == null ||
                _targetFooterHeight <= PositionTolerance)
            {
                return;
            }

            if (_footerLayoutElement == null)
            {
                _footerLayoutElement = _footer.gameObject.AddComponent<LayoutElement>();
            }

            _footerLayoutElement.minHeight = _targetFooterHeight;
            _footerLayoutElement.preferredHeight = _targetFooterHeight;
            _footerLayoutElement.flexibleHeight = 0f;

            // SquadMakerResponsiveLayoutGuard runs first and may restore the authored footer height.
            // Rebuild the real MainPanel owner after applying the measured LayoutElement contract so
            // the flexible body receives exactly the reclaimed height in the same frame.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_mainPanel);
            Canvas.ForceUpdateCanvases();

            RestoreBottomRelativeBranches();
        }

        private void RestoreBottomRelativeBranches()
        {
            if (_footer == null)
            {
                return;
            }

            for (int index = 0; index < _branchReferences.Count; index++)
            {
                FooterBranchReference reference = _branchReferences[index];
                if (reference == null || reference.Rect == null || reference.Rect.parent != _footer)
                {
                    continue;
                }

                float correction = CalculateBottomRelativeCorrection(
                    _footer,
                    reference.Rect,
                    reference.BottomOffset);
                if (Mathf.Abs(correction) <= PositionTolerance)
                {
                    continue;
                }

                Vector2 position = reference.Rect.anchoredPosition;
                position.y += correction;
                reference.Rect.anchoredPosition = position;
            }
        }

        internal static float CalculateFooterControlEnvelopeHeight(RectTransform footer)
        {
            if (footer == null)
            {
                return 0f;
            }

            float authoredHeight = Mathf.Abs(footer.rect.height);
            float requiredHeight = 0f;
            bool foundControl = false;
            Button[] controls = footer.GetComponentsInChildren<Button>(true);
            for (int index = 0; index < controls.Length; index++)
            {
                RectTransform control = controls[index] != null
                    ? controls[index].transform as RectTransform
                    : null;
                if (control == null)
                {
                    continue;
                }

                Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                    footer,
                    control);
                float bottomToTop = bounds.max.y - footer.rect.yMin;
                if (bottomToTop <= PositionTolerance)
                {
                    continue;
                }

                requiredHeight = Mathf.Max(requiredHeight, bottomToTop);
                foundControl = true;
            }

            if (!foundControl)
            {
                return authoredHeight;
            }

            return authoredHeight > PositionTolerance
                ? Mathf.Clamp(requiredHeight, 1f, authoredHeight)
                : requiredHeight;
        }

        internal static float CalculateBottomOffset(
            RectTransform footer,
            RectTransform branch)
        {
            if (footer == null || branch == null)
            {
                return 0f;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, branch);
            return bounds.min.y - footer.rect.yMin;
        }

        internal static float CalculateBottomRelativeCorrection(
            RectTransform footer,
            RectTransform branch,
            float referenceBottomOffset)
        {
            if (footer == null || branch == null)
            {
                return 0f;
            }

            float currentBottomOffset = CalculateBottomOffset(footer, branch);
            return referenceBottomOffset - currentBottomOffset;
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
    }
}
