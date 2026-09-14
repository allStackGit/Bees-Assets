using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Applies presentation-only feedback to tutorial panels without changing their authored width
    /// or horizontal padding. It also normalizes Squad Maker scrolling for pointer-over-label input.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class TutorialFeedbackPolishGuard : MonoBehaviour
    {
        private const float HorizontalPadding = 22f;
        private const float VerticalPadding = 18f;
        private const float SequenceFooterHeight = 34f;
        private const float FallbackMinimumTutorialHeight = 90f;
        private const float ScrollSensitivityMultiplier = 0.75f;
        private const string InnerBorderName = "Tutorial Inner Border";
        private static readonly Color TutorialBorderColor = new Color(0.48f, 0.55f, 0.61f, 0.9f);
        private static readonly Color InnerBorderColor = new Color(0.20f, 0.24f, 0.28f, 0.95f);
        private static readonly Color InfoTabFillColor = new Color(0.34f, 0.39f, 0.44f, 0.96f);

        private float _nextRefresh;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject existing = GameObject.Find("Tutorial Feedback Polish Guard");
            if (existing != null)
            {
                return;
            }

            GameObject host = new GameObject("Tutorial Feedback Polish Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<TutorialFeedbackPolishGuard>();
        }

        private void LateUpdate()
        {
            if (Time.unscaledTime < _nextRefresh)
            {
                return;
            }
            _nextRefresh = Time.unscaledTime + 0.1f;

            float minimumHeight = GetMinimumTutorialHeight();
            Tooltip[] tooltips = FindObjectsOfType<Tooltip>(true);
            for (int i = 0; i < tooltips.Length; i++)
            {
                PolishTooltip(tooltips[i], minimumHeight);
            }

            if (SceneManager.GetActiveScene().name == "Squad Maker")
            {
                PolishSquadMakerScrolling();
            }
        }

        private static void PolishTooltip(Tooltip tooltip, float minimumHeight)
        {
            if (tooltip == null || tooltip.TooltipText == null || tooltip.TooltipSize == null ||
                tooltip.TooltipObject == null || !tooltip.TooltipObject.activeInHierarchy)
            {
                return;
            }

            string formatted = PutSentencesOnSeparateLines(tooltip.TooltipText.text);
            if (tooltip.TooltipText.text != formatted)
            {
                tooltip.TooltipText.text = formatted;
            }

            float width = tooltip.TooltipSize.sizeDelta.x;
            if (width <= 0f)
            {
                width = tooltip.TooltipSize.rect.width;
            }
            float contentWidth = Mathf.Max(1f, width - HorizontalPadding * 2f);
            float preferredHeight = tooltip.TooltipText.GetPreferredValues(formatted, contentWidth, 0f).y;
            Transform footer = tooltip.TooltipSize.Find("Tutorial Sequence Footer");
            float footerHeight = footer != null && footer.gameObject.activeSelf ? SequenceFooterHeight : 0f;
            float requiredHeight = preferredHeight + VerticalPadding * 2f + footerHeight;
            Vector2 size = tooltip.TooltipSize.sizeDelta;
            size.y = Mathf.Max(minimumHeight, requiredHeight);
            tooltip.TooltipSize.sizeDelta = size;

            ConfigureDoubleBorder(tooltip);
            StyleSequenceButton(tooltip.TooltipSize.Find("Tutorial Sequence Footer/Previous"));
            StyleSequenceButton(tooltip.TooltipSize.Find("Tutorial Sequence Footer/Next"));
        }

        private static float GetMinimumTutorialHeight()
        {
            DialogueManager[] managers = FindObjectsOfType<DialogueManager>(true);
            for (int i = 0; i < managers.Length; i++)
            {
                DialogueManager manager = managers[i];
                if (manager == null || manager.DialogueBox == null)
                {
                    continue;
                }

                RectTransform dialogueRect = manager.DialogueBox.GetComponent<RectTransform>();
                if (dialogueRect == null)
                {
                    continue;
                }

                float dialogueHeight = dialogueRect.rect.height * Mathf.Abs(dialogueRect.localScale.y);
                if (dialogueHeight > 0f)
                {
                    return Mathf.Max(FallbackMinimumTutorialHeight, dialogueHeight * 0.5f);
                }
            }
            return FallbackMinimumTutorialHeight;
        }

        private static void ConfigureDoubleBorder(Tooltip tooltip)
        {
            Graphic body = tooltip.TooltipSize.GetComponent<Graphic>();
            if (body == null)
            {
                return;
            }

            Outline[] outlines = body.GetComponents<Outline>();
            Outline outer = outlines.Length > 0 ? outlines[0] : body.gameObject.AddComponent<Outline>();
            outer.effectColor = TutorialBorderColor;
            outer.effectDistance = new Vector2(3f, -3f);
            outer.useGraphicAlpha = true;
            for (int i = 1; i < outlines.Length; i++)
            {
                outlines[i].enabled = false;
            }

            Transform existing = tooltip.TooltipSize.Find(InnerBorderName);
            GameObject innerObject;
            if (existing == null)
            {
                innerObject = new GameObject(
                    InnerBorderName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image),
                    typeof(Outline));
                innerObject.transform.SetParent(tooltip.TooltipSize, false);
                innerObject.transform.SetAsFirstSibling();
            }
            else
            {
                innerObject = existing.gameObject;
            }

            RectTransform rect = innerObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(4f, 4f);
            rect.offsetMax = new Vector2(-4f, -4f);

            Image image = innerObject.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0f);
            image.raycastTarget = false;

            Outline inner = innerObject.GetComponent<Outline>();
            inner.effectColor = InnerBorderColor;
            inner.effectDistance = new Vector2(1.5f, -1.5f);
            inner.useGraphicAlpha = false;
        }

        private static void StyleSequenceButton(Transform buttonTransform)
        {
            if (buttonTransform == null)
            {
                return;
            }
            Image image = buttonTransform.GetComponent<Image>();
            if (image != null)
            {
                image.color = InfoTabFillColor;
            }
        }

        internal static string PutSentencesOnSeparateLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            System.Text.StringBuilder result = new System.Text.StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                result.Append(current);
                if ((current == '.' || current == '?' || current == '!') &&
                    i + 1 < text.Length && text[i + 1] == ' ')
                {
                    while (i + 1 < text.Length && text[i + 1] == ' ')
                    {
                        i++;
                    }
                    result.Append('\n');
                }
            }
            return result.ToString();
        }

        private static void PolishSquadMakerScrolling()
        {
            ScrollRect[] scrollRects = FindObjectsOfType<ScrollRect>(true);
            for (int i = 0; i < scrollRects.Length; i++)
            {
                ScrollRect scrollRect = scrollRects[i];
                if (scrollRect.GetComponent<SquadListScrollPolishedMarker>() == null)
                {
                    scrollRect.scrollSensitivity *= ScrollSensitivityMultiplier;
                    scrollRect.gameObject.AddComponent<SquadListScrollPolishedMarker>();
                }

                // Pointer scrolling is dispatched to the first graphic under the mouse. Give every
                // child graphic in the list a forwarding handler so scrolling works over the row
                // background, icon, or text instead of only over blank viewport space.
                Graphic[] graphics = scrollRect.GetComponentsInChildren<Graphic>(true);
                for (int graphicIndex = 0; graphicIndex < graphics.Length; graphicIndex++)
                {
                    GameObject graphicObject = graphics[graphicIndex].gameObject;
                    if (graphicObject == scrollRect.gameObject)
                    {
                        continue;
                    }
                    if (graphicObject.GetComponent<SquadListScrollForwarder>() == null)
                    {
                        graphicObject.AddComponent<SquadListScrollForwarder>();
                    }
                }
            }
        }
    }

    internal sealed class SquadListScrollPolishedMarker : MonoBehaviour
    {
    }

    internal sealed class SquadListScrollForwarder : MonoBehaviour, IScrollHandler
    {
        private ScrollRect _scrollRect;

        public void OnScroll(PointerEventData eventData)
        {
            if (_scrollRect == null)
            {
                _scrollRect = GetComponentInParent<ScrollRect>();
            }
            if (_scrollRect != null)
            {
                _scrollRect.OnScroll(eventData);
            }
        }
    }
}
