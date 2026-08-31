using Assets.Scripts.Scenes;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Applies the semantic horizontal placement of the Squad Maker START/TEST hover descriptions.
    ///
    /// SquadMakerInteractionGuard owns the root-canvas overlay and vertical above/below placement.
    /// A centered outer tooltip is not the desired horizontal presentation for the paired footer
    /// actions: START should expand left from its button and TEST should expand right. Screen-edge
    /// safety is based on the rendered TMP glyph bounds, not unused space in the outer tooltip rect.
    /// </summary>
    [DefaultExecutionOrder(-625)]
    public sealed class SquadMakerHoverPlacementGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const string HoverOverlayName = "Squad Maker Hover Text Overlay";
        private const float OverlayMargin = 8f;
        private const float BoundsTolerance = 0.01f;

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
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            ApplyPlacement(_squadMaker.StartButton, _squadMaker.StartText, -1);
            ApplyPlacement(_squadMaker.TestButton, _squadMaker.TestText, 1);
        }

        private static void ApplyPlacement(
            GameObject buttonObject,
            GameObject descriptionObject,
            int horizontalDirection)
        {
            RectTransform button = buttonObject != null ? buttonObject.transform as RectTransform : null;
            RectTransform description = descriptionObject != null
                ? descriptionObject.transform as RectTransform
                : null;
            RectTransform overlay = description != null ? description.parent as RectTransform : null;
            if (button == null || description == null || overlay == null ||
                overlay.name != HoverOverlayName)
            {
                return;
            }

            Rect buttonRect = GetRectInLocalSpace(button, overlay);
            Rect visibleLocalBounds;
            if (!TryGetRenderedTextBounds(descriptionObject, description, out visibleLocalBounds))
            {
                visibleLocalBounds = description.rect;
            }

            float targetX = CalculateDirectionalHoverX(
                buttonRect,
                Mathf.Abs(description.rect.width),
                visibleLocalBounds,
                overlay.rect,
                horizontalDirection,
                OverlayMargin);

            Vector2 position = description.anchoredPosition;
            if (Mathf.Abs(position.x - targetX) > BoundsTolerance)
            {
                position.x = targetX;
                description.anchoredPosition = position;
            }
        }

        internal static float CalculateDirectionalHoverX(
            Rect buttonRect,
            float descriptionWidth,
            Rect visibleLocalBounds,
            Rect overlayRect,
            int horizontalDirection,
            float margin = OverlayMargin)
        {
            float width = Mathf.Abs(descriptionWidth);
            float halfWidth = width * 0.5f;
            float desiredX;
            if (horizontalDirection < 0)
            {
                // START: keep the tooltip's right edge attached to the START button's right edge.
                desiredX = buttonRect.xMax - halfWidth;
            }
            else if (horizontalDirection > 0)
            {
                // TEST: keep the tooltip's left edge attached to the TEST button's left edge.
                desiredX = buttonRect.xMin + halfWidth;
            }
            else
            {
                desiredX = buttonRect.center.x;
            }

            float safeMargin = Mathf.Max(0f, margin);
            float minimumX = overlayRect.xMin + safeMargin - visibleLocalBounds.xMin;
            float maximumX = overlayRect.xMax - safeMargin - visibleLocalBounds.xMax;
            if (minimumX > maximumX)
            {
                // The visible content itself is wider than the safe region. Center it rather than
                // choosing one edge and making the opposite edge even worse.
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
