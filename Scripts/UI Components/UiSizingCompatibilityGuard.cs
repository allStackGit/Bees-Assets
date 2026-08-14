using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    [DefaultExecutionOrder(2000)]
    internal sealed class UiSizingCompatibilityGuard : MonoBehaviour
    {
        private const float ScanInterval = 0.1f;
        private float _nextScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("UI Sizing Compatibility Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<UiSizingCompatibilityGuard>();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan)
            {
                return;
            }
            _nextScan = Time.unscaledTime + ScanInterval;

            foreach (Button button in FindObjectsOfType<Button>(true))
            {
                if (button == null)
                {
                    continue;
                }

                if (button.gameObject.name == "Close Button")
                {
                    RectTransform rect = button.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        rect.sizeDelta = new Vector2(16f, 16f);
                    }
                }

                TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null && label.text.Trim().ToUpperInvariant() == "COLOR" &&
                    button.GetComponent<SquadColorButtonInsetMarker>() == null)
                {
                    RectTransform rect = button.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        rect.sizeDelta = new Vector2(
                            Mathf.Max(1f, rect.sizeDelta.x - 4f),
                            Mathf.Max(1f, rect.sizeDelta.y - 4f));
                    }
                    button.gameObject.AddComponent<SquadColorButtonInsetMarker>();
                }
            }
        }
    }

    internal sealed class SquadColorButtonInsetMarker : MonoBehaviour
    {
    }
}
