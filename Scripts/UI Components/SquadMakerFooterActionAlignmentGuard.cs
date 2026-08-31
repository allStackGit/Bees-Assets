using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the Squad Maker START/TEST action row attached to the body/footer boundary.
    ///
    /// The authored hierarchy is Footer -> Right Side -> Start Buttons -> START/TEST. The nested
    /// top-relative offsets place the button tops below the Footer top even though the Footer itself
    /// correctly owns the bottom 51 logical units. This guard solves that semantic relationship
    /// directly: keep the complete Footer and translate only the shared START/TEST row until the
    /// highest active action button meets the Footer top. The calculation is idempotent and uses the
    /// current rendered relationship, so repeated responsive passes cannot accumulate drift.
    /// </summary>
    [DefaultExecutionOrder(-660)]
    public sealed class SquadMakerFooterActionAlignmentGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string FooterName = "Footer";
        private const float PositionTolerance = 0.01f;

        private SquadMaker _squadMaker;
        private RectTransform _footer;
        private RectTransform _actionRow;
        private RectTransform _startButton;
        private RectTransform _testButton;

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

            _squadMaker = squadMaker;
            ResolveOwnedGeometry();
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            if (_footer == null || _actionRow == null || _startButton == null || _testButton == null)
            {
                ResolveOwnedGeometry();
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

        private void ApplyAlignment()
        {
            if (_footer == null || _actionRow == null)
            {
                return;
            }

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

            _actionRow.anchoredPosition += offset;
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
