using Assets.Scripts.UIComponents;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    internal sealed class SummaryClosePressGuard : MonoBehaviour
    {
        private const string ContinueButtonName = "Continue Button";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Summary Close Press Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<SummaryClosePressGuard>();
        }

        private void Update()
        {
            GameMenus menus = FindObjectOfType<GameMenus>();
            if (menus == null || menus.SummaryPanel == null ||
                menus.SummaryPanel.GetComponent<SummaryClosePressMarker>() != null)
            {
                return;
            }

            // The summary continuation control must not depend on the legacy close control having
            // already been upgraded to a Unity Button. The exact close hit area is owned by the
            // initial-design regression guard, so do not add a second PointerDown interaction path.
            EnsureContinueButton(menus);
            menus.SummaryPanel.AddComponent<SummaryClosePressMarker>();
        }

        private static void EnsureContinueButton(GameMenus menus)
        {
            Transform existing = menus.SummaryPanel.transform.Find(ContinueButtonName);
            GameObject buttonObject = existing != null ? existing.gameObject : null;
            if (buttonObject == null)
            {
                buttonObject = new GameObject(
                    ContinueButtonName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image),
                    typeof(Button));
                buttonObject.transform.SetParent(menus.SummaryPanel.transform, false);

                RectTransform rect = buttonObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0f);
                rect.anchorMax = new Vector2(0.5f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.anchoredPosition = new Vector2(0f, 18f);
                rect.sizeDelta = new Vector2(160f, 38f);

                Image background = buttonObject.GetComponent<Image>();
                background.color = new Color(0.20f, 0.24f, 0.28f, 0.96f);
            }

            Button button = buttonObject.GetComponent<Button>();
            if (button == null)
            {
                button = buttonObject.AddComponent<Button>();
            }
            Image image = buttonObject.GetComponent<Image>();
            if (image == null)
            {
                image = buttonObject.AddComponent<Image>();
                image.color = new Color(0.20f, 0.24f, 0.28f, 0.96f);
            }
            button.targetGraphic = image;
            button.onClick.RemoveListener(menus.HideMissionSummary);
            button.onClick.AddListener(menus.HideMissionSummary);

            TMP_Text label = buttonObject.GetComponentInChildren<TMP_Text>(true);
            if (label == null)
            {
                GameObject labelObject = new GameObject(
                    "Label",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(TextMeshProUGUI));
                labelObject.transform.SetParent(buttonObject.transform, false);

                RectTransform labelRect = labelObject.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
                label = labelObject.GetComponent<TextMeshProUGUI>();
            }

            TMP_Text summaryText = menus.SummaryPanel.GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(text => text != null && text != label);
            if (summaryText != null)
            {
                label.font = summaryText.font;
            }
            label.text = "NEXT";
            label.fontSize = 18f;
            label.fontStyle = FontStyles.Bold;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;

            buttonObject.transform.SetAsLastSibling();
        }
    }

    internal sealed class SummaryClosePressMarker : MonoBehaviour
    {
    }
}
