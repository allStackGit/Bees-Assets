using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.UIComponents;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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

            Button closeButton = menus.SummaryPanel.GetComponentsInChildren<Button>(true)
                .FirstOrDefault(button => button != null && button.gameObject.name == "Close Button");
            if (closeButton == null)
            {
                return;
            }

            ConfigureClosePress(menus, closeButton);
            EnsureContinueButton(menus);
            menus.SummaryPanel.AddComponent<SummaryClosePressMarker>();
        }

        private static void ConfigureClosePress(GameMenus menus, Button closeButton)
        {
            EventTrigger trigger = closeButton.GetComponent<EventTrigger>();
            if (trigger == null)
            {
                trigger = closeButton.gameObject.AddComponent<EventTrigger>();
            }
            if (trigger.triggers == null)
            {
                trigger.triggers = new List<EventTrigger.Entry>();
            }

            EventTrigger.Entry press = new EventTrigger.Entry
            {
                eventID = EventTriggerType.PointerDown
            };
            press.callback.AddListener(_ =>
            {
                if (menus.SummaryPanel.activeInHierarchy)
                {
                    menus.HideMissionSummary();
                }
            });
            trigger.triggers.Add(press);
        }

        private static void EnsureContinueButton(GameMenus menus)
        {
            Transform existing = menus.SummaryPanel.transform.Find(ContinueButtonName);
            if (existing != null)
            {
                return;
            }

            GameObject buttonObject = new GameObject(
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

            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(menus.HideMissionSummary);

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

            TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
            TMP_Text summaryText = menus.SummaryPanel.GetComponentInChildren<TMP_Text>(true);
            if (summaryText != null)
            {
                label.font = summaryText.font;
            }
            label.text = "CONTINUE";
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
