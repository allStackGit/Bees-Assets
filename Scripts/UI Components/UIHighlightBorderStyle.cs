using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Keeps the shared UI highlight prefab unchanged for its normal uses, but renders it as a
    /// non-blocking red outline when it is attached directly to the Attack on Sight button.
    /// </summary>
    public sealed class UIHighlightBorderStyle : MonoBehaviour
    {
        private const float BorderThickness = 2f;
        private bool _styled;

        private void Awake()
        {
            TryApplyAttackOnSightStyle();
        }

        private void Start()
        {
            // The Instantiate(original, parent) overload normally establishes the parent before
            // Awake. Keep this fallback so the style is still applied if lifecycle ordering changes.
            TryApplyAttackOnSightStyle();
        }

        private void TryApplyAttackOnSightStyle()
        {
            if (_styled)
            {
                return;
            }

            SquadActionBox actionBox = GetComponentInParent<SquadActionBox>();
            if (actionBox == null || actionBox.AttackOnSightButton == null ||
                transform.parent != actionBox.AttackOnSightButton.transform)
            {
                return;
            }

            Image fill = GetComponent<Image>();
            if (fill != null)
            {
                fill.enabled = false;
                fill.raycastTarget = false;
            }

            CreateEdge(
                "Border Top",
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -BorderThickness),
                Vector2.zero);
            CreateEdge(
                "Border Bottom",
                Vector2.zero,
                new Vector2(1f, 0f),
                Vector2.zero,
                new Vector2(0f, BorderThickness));
            CreateEdge(
                "Border Left",
                Vector2.zero,
                new Vector2(0f, 1f),
                Vector2.zero,
                new Vector2(BorderThickness, 0f));
            CreateEdge(
                "Border Right",
                new Vector2(1f, 0f),
                Vector2.one,
                new Vector2(-BorderThickness, 0f),
                Vector2.zero);

            _styled = true;
        }

        private void CreateEdge(
            string edgeName,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            GameObject edge = new GameObject(edgeName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            edge.transform.SetParent(transform, false);

            RectTransform edgeRect = edge.GetComponent<RectTransform>();
            edgeRect.anchorMin = anchorMin;
            edgeRect.anchorMax = anchorMax;
            edgeRect.offsetMin = offsetMin;
            edgeRect.offsetMax = offsetMax;

            Image edgeImage = edge.GetComponent<Image>();
            edgeImage.color = Color.red;
            edgeImage.raycastTarget = false;
        }
    }
}
