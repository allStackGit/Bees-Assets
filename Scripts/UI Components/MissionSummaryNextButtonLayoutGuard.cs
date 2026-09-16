using TMPro;
using UnityEngine;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Normalizes the runtime-created mission-summary continuation control after all legacy guards
    /// have run. Existing code only sized newly-created buttons, so serialized/runtime survivors
    /// could retain a much larger layout and hang outside the green summary panel.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    internal sealed class MissionSummaryNextButtonLayoutGuard : MonoBehaviour
    {
        private const string ContinueButtonName = "Continue Button";
        private static readonly Vector2 ButtonSize = new Vector2(116f, 30f);
        private static readonly Vector2 ButtonPosition = new Vector2(0f, 9f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Mission Summary Next Button Layout Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<MissionSummaryNextButtonLayoutGuard>();
        }

        private void LateUpdate()
        {
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

            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = ButtonPosition;
            rect.sizeDelta = ButtonSize;
            rect.localScale = Vector3.one;

            TMP_Text label = rect.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.fontSize = 14f;
                label.enableAutoSizing = false;
                label.alignment = TextAlignmentOptions.Center;
            }
        }
    }
}
