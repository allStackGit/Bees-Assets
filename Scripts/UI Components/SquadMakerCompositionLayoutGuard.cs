using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Owns the edge-relative controls inside Squad Composition.
    ///
    /// SquadMakerResponsiveLayoutGuard owns the size of the composition panel itself. These direct
    /// children are intentionally not LayoutGroup-owned, so their anchors must express which edge
    /// they belong to when the composition grows beyond its authored 620x420 rectangle.
    /// </summary>
    [DefaultExecutionOrder(-650)]
    public sealed class SquadMakerCompositionLayoutGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string SquadCompositionName = "Squad Composition";
        private const string FormationsName = "Formations";
        private const string LowerButtonsName = "Lower Buttons";

        private const float ReferenceCompositionWidth = 620f;
        private const float ReferenceCompositionHeight = 420f;

        private SquadMaker _squadMaker;

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

                SquadMakerCompositionLayoutGuard guard =
                    squadMaker.GetComponent<SquadMakerCompositionLayoutGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerCompositionLayoutGuard>();
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
            RectTransform squadShipCount = squadMaker.SquadShipCount != null
                ? squadMaker.SquadShipCount.transform as RectTransform
                : null;
            RectTransform composition = FindAncestorByName(squadShipCount, SquadCompositionName);
            if (composition == null)
            {
                return;
            }

            RectTransform formations = FindDirectChildByName(composition, FormationsName);
            RectTransform lowerButtons = FindDirectChildByName(composition, LowerButtonsName);
            RectTransform squadShipCountOwner = FindDirectChildAncestor(squadShipCount, composition);

            ApplyReferenceEdgePins(
                composition,
                formations,
                lowerButtons,
                squadShipCountOwner);
        }

        /// <summary>
        /// Converts the three authored controls from reference-rectangle placement into the edge
        /// ownership that their visual roles require. Only the owned axes are changed; child layout
        /// and unrelated axes remain authored exactly as before.
        /// </summary>
        internal static void ApplyReferenceEdgePins(
            RectTransform composition,
            RectTransform formations,
            RectTransform lowerButtons,
            RectTransform squadShipCount)
        {
            if (composition == null)
            {
                return;
            }

            if (formations != null)
            {
                Vector2 anchorMin = formations.anchorMin;
                Vector2 anchorMax = formations.anchorMax;
                Vector2 anchoredPosition = formations.anchoredPosition;

                anchorMin.x = 0f;
                anchorMax.x = 0f;
                anchoredPosition.x = formations.pivot.x * Mathf.Abs(formations.rect.width);

                formations.anchorMin = anchorMin;
                formations.anchorMax = anchorMax;
                formations.anchoredPosition = anchoredPosition;
            }

            if (lowerButtons != null)
            {
                Vector2 anchorMin = lowerButtons.anchorMin;
                Vector2 anchorMax = lowerButtons.anchorMax;
                Vector2 anchoredPosition = lowerButtons.anchoredPosition;
                float referencePivotFromLeft = CalculateReferencePivotCoordinate(
                    anchorMin.x,
                    anchorMax.x,
                    lowerButtons.pivot.x,
                    anchoredPosition.x,
                    ReferenceCompositionWidth);
                float referencePivotFromBottom = CalculateReferencePivotCoordinate(
                    anchorMin.y,
                    anchorMax.y,
                    lowerButtons.pivot.y,
                    anchoredPosition.y,
                    ReferenceCompositionHeight);

                anchorMin.x = 0f;
                anchorMax.x = 0f;
                anchorMin.y = 0f;
                anchorMax.y = 0f;
                anchoredPosition.x = referencePivotFromLeft;
                anchoredPosition.y = referencePivotFromBottom;

                lowerButtons.anchorMin = anchorMin;
                lowerButtons.anchorMax = anchorMax;
                lowerButtons.anchoredPosition = anchoredPosition;
            }

            if (squadShipCount != null)
            {
                Vector2 anchorMin = squadShipCount.anchorMin;
                Vector2 anchorMax = squadShipCount.anchorMax;
                Vector2 anchoredPosition = squadShipCount.anchoredPosition;
                float referencePivotFromLeft = CalculateReferencePivotCoordinate(
                    anchorMin.x,
                    anchorMax.x,
                    squadShipCount.pivot.x,
                    anchoredPosition.x,
                    ReferenceCompositionWidth);

                anchorMin.x = 0f;
                anchorMax.x = 0f;
                anchoredPosition.x = referencePivotFromLeft;

                squadShipCount.anchorMin = anchorMin;
                squadShipCount.anchorMax = anchorMax;
                squadShipCount.anchoredPosition = anchoredPosition;
            }
        }

        internal static float CalculateReferencePivotCoordinate(
            float authoredAnchorMin,
            float authoredAnchorMax,
            float pivot,
            float authoredAnchoredPosition,
            float referenceOwnerSize)
        {
            float anchorReference = Mathf.Lerp(authoredAnchorMin, authoredAnchorMax, pivot);
            return anchorReference * referenceOwnerSize + authoredAnchoredPosition;
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

            for (int index = 0; index < owner.childCount; index++)
            {
                RectTransform child = owner.GetChild(index) as RectTransform;
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
    }
}
