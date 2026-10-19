using Assets.Scripts.UIComponents;
using Assets.Scripts;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Owns the campaign summary footer independently of the statistics layout above it.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    internal sealed class MissionSummaryNextButtonLayoutGuard : MonoBehaviour
    {
        private const string ContinueButtonName = "Continue Button";
        private const int FooterHeight = 48;
        private static readonly Vector2 ButtonSize = new Vector2(76f, 28f);
        private static readonly Vector2 ButtonPosition = new Vector2(-12f, 12f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Mission Summary Next Button Layout Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<MissionSummaryNextButtonLayoutGuard>();
        }

        private void LateUpdate()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                return;
            }

            GameMenus menus = FindObjectOfType<GameMenus>();
            if (menus == null || menus.SummaryPanel == null)
            {
                return;
            }

            Transform buttonTransform = menus.SummaryPanel.transform.Find(ContinueButtonName);
            RectTransform rect = buttonTransform as RectTransform;
            if (rect == null)
            {
                return;
            }

            // The summary's VerticalLayoutGroup owns the statistics, not this footer control.
            LayoutElement footer = rect.GetComponent<LayoutElement>();
            if (footer == null)
            {
                footer = rect.gameObject.AddComponent<LayoutElement>();
            }
            footer.ignoreLayout = true;

            RectTransform panel = menus.SummaryPanel.transform as RectTransform;
            VerticalLayoutGroup statistics = menus.SummaryPanel.GetComponent<VerticalLayoutGroup>();
            if (panel != null && statistics != null)
            {
                if (statistics.padding.bottom != FooterHeight)
                {
                    RectOffset padding = statistics.padding;
                    statistics.padding = new RectOffset(padding.left, padding.right, padding.top, FooterHeight);
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                float requiredHeight = LayoutUtility.GetPreferredHeight(panel);
                if (requiredHeight > 0f && !Mathf.Approximately(panel.rect.height, requiredHeight))
                {
                    panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);
                }
            }

            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = ButtonPosition;
            rect.sizeDelta = ButtonSize;
            rect.localScale = Vector3.one;

            Image background = rect.GetComponent<Image>();
            Button button = rect.GetComponent<Button>();
            Button reference = menus.GameSpeedButton != null ? menus.GameSpeedButton.GetComponent<Button>() : null;
            if (background != null && button != null && reference != null && reference.targetGraphic is Image referenceImage)
            {
                background.sprite = referenceImage.sprite;
                background.type = Image.Type.Sliced;
                background.color = referenceImage.color;
                background.pixelsPerUnitMultiplier = referenceImage.pixelsPerUnitMultiplier;
                button.targetGraphic = background;
                button.transition = reference.transition;
                button.colors = reference.colors;

                Outline border = rect.GetComponent<Outline>();
                if (border != null)
                {
                    border.enabled = false;
                }
            }

            TMP_Text label = rect.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.fontSize = 14f;
                label.fontStyle = FontStyles.Normal;
                label.enableAutoSizing = false;
                label.alignment = TextAlignmentOptions.Center;
                if (menus.GameSpeedButtonText != null)
                {
                    label.color = menus.GameSpeedButtonText.color;
                    label.fontStyle = menus.GameSpeedButtonText.fontStyle;
                }
            }

            PlaceCloseBelowHeader(panel);
        }

        private static void PlaceCloseBelowHeader(RectTransform panel)
        {
            if (panel == null)
            {
                return;
            }
            foreach (Button close in panel.GetComponentsInChildren<Button>(true))
            {
                if (close == null || close.name.IndexOf("close", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                RectTransform closeRect = close.transform as RectTransform;
                if (closeRect == null)
                {
                    continue;
                }
                LayoutElement layout = closeRect.GetComponent<LayoutElement>();
                if (layout == null)
                {
                    layout = closeRect.gameObject.AddComponent<LayoutElement>();
                }
                layout.ignoreLayout = true;
                Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(panel, closeRect);
                // The authored header bar is 15 units high. Leave six more units before the X.
                Vector3 correction = new Vector3(
                    panel.rect.xMax - 8f - bounds.max.x,
                    panel.rect.yMax - 21f - bounds.max.y,
                    0f);
                closeRect.position += panel.TransformVector(correction);
                return;
            }
        }
    }
}
