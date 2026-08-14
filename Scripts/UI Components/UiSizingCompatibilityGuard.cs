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

                TMP_Text tmpLabel = button.GetComponentInChildren<TMP_Text>(true);
                Text legacyLabel = button.GetComponentInChildren<Text>(true);
                bool hasTextLabel =
                    (tmpLabel != null && !string.IsNullOrWhiteSpace(tmpLabel.text)) ||
                    (legacyLabel != null && !string.IsNullOrWhiteSpace(legacyLabel.text));

                // The shared image-only X prefab is authored at 16x16. Some unrelated controls,
                // including the Squad Maker ship-builder CLOSE button, also use the legacy object
                // name "Close Button" but contain text and must retain their authored dimensions.
                if (button.gameObject.name == "Close Button" && !hasTextLabel)
                {
                    RectTransform rect = button.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        rect.sizeDelta = new Vector2(16f, 16f);
                    }
                }

                if (tmpLabel != null && tmpLabel.text.Trim().ToUpperInvariant() == "COLOR" &&
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
