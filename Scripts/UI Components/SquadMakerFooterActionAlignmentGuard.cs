using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the START/TEST action row flush with the top edge of the authored Squad Maker footer.
    ///
    /// START/TEST are not children of the Chosen Squads column. They live in Footer/Right Side/
    /// Start Buttons, and the serialized Right Side inset leaves the button tops below the footer top.
    /// The responsive body ends at that footer top, so the inset otherwise appears as a horizontal
    /// strip between Supply Capacity and the buttons. Preserve the 51-unit footer and all unrelated
    /// footer controls; only translate the shared START/TEST row from its immutable authored position.
    /// </summary>
    [DefaultExecutionOrder(-625)]
    public sealed class SquadMakerFooterActionAlignmentGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string FooterName = "Footer";
        private const float RepairInterval = 0.20f;
        private const float PositionTolerance = 0.01f;

        private SquadMaker _squadMaker;
        private RectTransform _footer;
        private RectTransform _actionRow;
        private RectTransform _startButton;
        private RectTransform _testButton;
        private Vector2 _authoredActionRowPosition;
        private float _nextRepairTime;
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

            if (_squadMaker == squadMaker && _referenceCaptured)
            {
                _nextRepairTime = 0f;
                return;
            }

            _squadMaker = squadMaker;
            _referenceCaptured = false;
            ResolveOwnedGeometry();
            CaptureReference();
            _nextRepairTime = 0f;
        }

        private void LateUpdate()
        {
            if (_squadMaker == null || Time.unscaledTime < _nextRepairTime)
            {
                return;
            }

            _nextRepairTime = Time.unscaledTime + RepairInterval;
            if (_footer == null || _actionRow == null || _startButton == null || _testButton == null)
            {
                ResolveOwnedGeometry();
            }
            if (!_referenceCaptured)
            {
                CaptureReference();
            }

            ApplyAlignment();
        }

        private void ResolveOwnedGeometry()
        {
            _startButton = _squadMaker != null && _squadMaker.StartButton != null
                ? _squadMaker.StartButton.transform as RectTransform
                : null;
            _testButton = _squadMaker != null && _squadMaker.TestButton != null
                ? _squadMaker.TestButton.transform as RectTransform
                : null;
            _actionRow = FindNearestCommonAncestor(_startButton, _testButton);
            _footer = FindAncestorByName(_actionRow, FooterName);
        }

        private void CaptureReference()
        {
            if (_referenceCaptured || _actionRow == null || _footer == null)
            {
                return;
            }

            _authoredActionRowPosition = _actionRow.anchoredPosition;
            _referenceCaptured = true;
        }

        private void ApplyAlignment()
        {
            if (!_referenceCaptured || _footer == null || _actionRow == null)
            {
                return;
            }

            // Always derive from the authored row position. Display changes and repeated repair passes
            // must never promote a previous responsive result to the next baseline.
            _actionRow.anchoredPosition = _authoredActionRowPosition;

            bool startVisible = _startButton != null && _startButton.gameObject.activeInHierarchy;
            bool testVisible = _testButton != null && _testButton.gameObject.activeInHierarchy;
            if (!startVisible && !testVisible)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_actionRow);
            Vector2 offset = CalculateFooterActionRowOffset(
                _footer,
                _actionRow,
                _startButton,
                _testButton);
            if (offset.sqrMagnitude <= PositionTolerance * PositionTolerance)
            {
                return;
            }

            _actionRow.anchoredPosition = _authoredActionRowPosition + offset;
        }

        internal static Vector2 CalculateFooterActionRowOffset(
            RectTransform footer,
            RectTransform actionRow,
            RectTransform firstButton,
            RectTransform secondButton)
        {
            RectTransform rowParent = actionRow != null ? actionRow.parent as RectTransform : null;
            if (footer == null || actionRow == null || rowParent == null)
            {
                return Vector2.zero;
            }

            float highestButtonTop = float.NegativeInfinity;
            highestButtonTop = Mathf.Max(
                highestButtonTop,
                CalculateActiveButtonTop(footer, firstButton));
            highestButtonTop = Mathf.Max(
                highestButtonTop,
                CalculateActiveButtonTop(footer, secondButton));
            if (float.IsNegativeInfinity(highestButtonTop))
            {
                return Vector2.zero;
            }

            float footerLocalDeltaY = footer.rect.yMax - highestButtonTop;
            Vector3 worldOrigin = footer.TransformPoint(Vector3.zero);
            Vector3 worldShifted = footer.TransformPoint(new Vector3(0f, footerLocalDeltaY, 0f));
            Vector3 parentOrigin = rowParent.InverseTransformPoint(worldOrigin);
            Vector3 parentShifted = rowParent.InverseTransformPoint(worldShifted);
            Vector3 parentDelta = parentShifted - parentOrigin;
            return new Vector2(parentDelta.x, parentDelta.y);
        }

        private static float CalculateActiveButtonTop(RectTransform footer, RectTransform button)
        {
            if (footer == null || button == null || !button.gameObject.activeInHierarchy)
            {
                return float.NegativeInfinity;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, button);
            return bounds.max.y;
        }

        private static RectTransform FindNearestCommonAncestor(
            RectTransform first,
            RectTransform second)
        {
            if (first == null || second == null)
            {
                return null;
            }

            Transform candidate = first.parent;
            while (candidate != null)
            {
                if (second.IsChildOf(candidate))
                {
                    return candidate as RectTransform;
                }
                candidate = candidate.parent;
            }

            return null;
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
