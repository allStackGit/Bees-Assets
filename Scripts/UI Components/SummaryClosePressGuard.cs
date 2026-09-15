using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.UIComponents;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    internal sealed class SummaryClosePressGuard : MonoBehaviour
    {
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
            menus.SummaryPanel.AddComponent<SummaryClosePressMarker>();
        }
    }

    internal sealed class SummaryClosePressMarker : MonoBehaviour
    {
    }
}
