using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    [DefaultExecutionOrder(2000)]
    internal sealed class UiInteractionCompatibilityGuard : MonoBehaviour
    {
        private const float ScanInterval = 0.1f;
        private float _nextScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("UI Interaction Compatibility Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<UiInteractionCompatibilityGuard>();
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
                        // Restore the authored visual size. The previous workaround enlarged the
                        // X, but missed clicks are caused by release-based Button semantics instead.
                        rect.sizeDelta = new Vector2(16f, 16f);
                    }
                    CloseButtonPointerDownCapture.Configure(button);
                }

                TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null && label.text.Trim().ToUpperInvariant() == "COLOR" &&
                    button.GetComponent<SquadColorButtonInsetMarker>() == null)
                {
                    RectTransform rect = button.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        // Keep the new two-pixel frame inside the surrounding Squad Maker panel.
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

    internal sealed class CloseButtonPointerDownCapture : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        private Button _button;
        private bool _capturingPress;

        internal static void Configure(Button button)
        {
            if (button != null && button.GetComponent<CloseButtonPointerDownCapture>() == null)
            {
                CloseButtonPointerDownCapture guard = button.gameObject.AddComponent<CloseButtonPointerDownCapture>();
                guard._button = button;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || _capturingPress ||
                _button == null || !_button.IsInteractable())
            {
                return;
            }

            // Unity Button normally commits on a complete click after pointer-up. For an X, a
            // small release drift should not cancel a press that clearly began on the control.
            // Disable the normal click path before invoking so the action cannot fire twice.
            _capturingPress = true;
            _button.interactable = false;
            _button.onClick.Invoke();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_capturingPress && eventData.button == PointerEventData.InputButton.Left)
            {
                StartCoroutine(ReenableAfterRelease());
            }
        }

        private IEnumerator ReenableAfterRelease()
        {
            yield return null;
            if (_button != null)
            {
                _button.interactable = true;
            }
            _capturingPress = false;
        }
    }
}
