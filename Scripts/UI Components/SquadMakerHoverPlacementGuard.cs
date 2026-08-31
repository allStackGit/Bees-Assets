using Assets.Scripts.Scenes;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Restores the semantic horizontal placement of the Squad Maker START/TEST hover descriptions.
    ///
    /// The serialized scene authors both help descriptions as centered content in the Chosen Squads
    /// column. SquadMakerInteractionGuard moves them into a root-canvas overlay for clipping safety and
    /// temporarily centers their outer rectangles on the hovered buttons. This final horizontal pass
    /// restores the authored presentation intent by centering the actually rendered TMP glyph bounds in
    /// the live Chosen Squads column, then clamps only those visible bounds to the root-canvas margin.
    /// Vertical placement remains owned by InteractionGuard and SupplyCapacityPresentationGuard.
    /// </summary>
    [DefaultExecutionOrder(-625)]
    public sealed class SquadMakerHoverPlacementGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string ChosenSquadsColumnName = "Chosen Squads Column";
        private const string HoverOverlayName = "Squad Maker Hover Text Overlay";
        private const float OverlayMargin = 8f;
        private const float BoundsTolerance = 0.01f;

        private SquadMaker _squadMaker;
        private RectTransform _chosenColumn;

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

                SquadMakerHoverPlacementGuard guard =
                    squadMaker.GetComponent<SquadMakerHoverPlacementGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerHoverPlacementGuard>();
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
            _squadMaker = squadMaker;
            _chosenColumn = ResolveChosenColumn(squadMaker);
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            if (_chosenColumn == null)
            {
                _chosenColumn = ResolveChosenColumn(_squadMaker);
            }

            ApplyPlacement(_squadMaker.StartText);
            ApplyPlacement(_squadMaker.TestText);
        }

        private void ApplyPlacement(GameObject descriptionObject)
        {
            RectTransform description = descriptionObject != null
                ? descriptionObject.transform as RectTransform
                : null;
            RectTransform overlay = description != null ? description.parent as RectTransform : null;
            if (description == null || overlay == null || _chosenColumn == null ||
                overlay.name != HoverOverlayName)
            {
                return;
            }

            Rect columnRect = GetRectInLocalSpace(_chosenColumn, overlay);
            Rect visibleLocalBounds;
            if (!TryGetRenderedTextBounds(descriptionObject, description, out visibleLocalBounds))
            {
                visibleLocalBounds = description.rect;
            }

            float targetX = CalculateColumnCenteredHoverX(
                columnRect,
                visibleLocalBounds,
                overlay.rect,
                OverlayMargin);

            Vector2 position = description.anchoredPosition;
            if (Mathf.Abs(position.x - targetX) > BoundsTolerance)
            {
                position.x = targetX;
                description.anchoredPosition = position;
            }
        }

        internal static float CalculateColumnCenteredHoverX(
            Rect columnRect,
            Rect visibleLocalBounds,
            Rect overlayRect,
            float margin = OverlayMargin)
        {
            // Center what the player can actually see, not the padded outer tooltip rectangle. TMP
            // glyph bounds are commonly asymmetric inside their RectTransform, so outer-rect centering
            // can still make the paragraph visibly lean left or right.
            float desiredX = columnRect.center.x - visibleLocalBounds.center.x;

            float safeMargin = Mathf.Max(0f, margin);
            float minimumX = overlayRect.xMin + safeMargin - visibleLocalBounds.xMin;
            float maximumX = overlayRect.xMax - safeMargin - visibleLocalBounds.xMax;
            if (minimumX > maximumX)
            {
                // The visible content itself is wider than the safe region. Center it in the safe
                // region so any unavoidable overflow is symmetric.
                return (minimumX + maximumX) * 0.5f;
            }

            return Mathf.Clamp(desiredX, minimumX, maximumX);
        }

        internal static bool TryGetRenderedTextBounds(
            GameObject descriptionObject,
            RectTransform description,
            out Rect visibleLocalBounds)
        {
            visibleLocalBounds = default(Rect);
            if (descriptionObject == null || description == null)
            {
                return false;
            }

            TMP_Text text = descriptionObject.GetComponentInChildren<TMP_Text>(true);
            if (text == null || text.rectTransform == null)
            {
                return false;
            }

            text.ForceMeshUpdate();
            Bounds bounds = text.textBounds;
            if (bounds.size.x <= BoundsTolerance || bounds.size.y <= BoundsTolerance)
            {
                return false;
            }

            RectTransform textRect = text.rectTransform;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;

            Vector3 first = description.InverseTransformPoint(
                textRect.TransformPoint(new Vector3(min.x, min.y, 0f)));
            float minX = first.x;
            float maxX = first.x;
            float minY = first.y;
            float maxY = first.y;

            IncludePoint(description, textRect, new Vector3(min.x, max.y, 0f),
                ref minX, ref maxX, ref minY, ref maxY);
            IncludePoint(description, textRect, new Vector3(max.x, min.y, 0f),
                ref minX, ref maxX, ref minY, ref maxY);
            IncludePoint(description, textRect, new Vector3(max.x, max.y, 0f),
                ref minX, ref maxX, ref minY, ref maxY);

            visibleLocalBounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return visibleLocalBounds.width > BoundsTolerance &&
                   visibleLocalBounds.height > BoundsTolerance;
        }

        private static void IncludePoint(
            RectTransform description,
            RectTransform source,
            Vector3 sourceLocalPoint,
            ref float minX,
            ref float maxX,
            ref float minY,
            ref float maxY)
        {
            Vector3 local = description.InverseTransformPoint(source.TransformPoint(sourceLocalPoint));
            minX = Mathf.Min(minX, local.x);
            maxX = Mathf.Max(maxX, local.x);
            minY = Mathf.Min(minY, local.y);
            maxY = Mathf.Max(maxY, local.y);
        }

        private static RectTransform ResolveChosenColumn(SquadMaker squadMaker)
        {
            RectTransform chosenList = squadMaker != null && squadMaker.ChosenSquadList != null
                ? squadMaker.ChosenSquadList.transform as RectTransform
                : null;
            return FindAncestorByName(chosenList, ChosenSquadsColumnName);
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

        private static Rect GetRectInLocalSpace(RectTransform rect, RectTransform owner)
        {
            Vector3[] worldCorners = new Vector3[4];
            rect.GetWorldCorners(worldCorners);
            Vector3 bottomLeft = owner.InverseTransformPoint(worldCorners[0]);
            Vector3 topRight = owner.InverseTransformPoint(worldCorners[2]);
            return Rect.MinMaxRect(bottomLeft.x, bottomLeft.y, topRight.x, topRight.y);
        }
    }
}
