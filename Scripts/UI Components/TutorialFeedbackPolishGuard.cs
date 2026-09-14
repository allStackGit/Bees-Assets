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
        private const float MinimumTutorialHeight = 90f;
        private const float ScrollSensitivityMultiplier = 0.75f;
        private static readonly Color TutorialBorderColor = new Color(0.48f, 0.55f, 0.61f, 0.9f);
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

            Tooltip[] tooltips = FindObjectsOfType<Tooltip>(true);
            for (int i = 0; i < tooltips.Length; i++)
            {
                PolishTooltip(tooltips[i]);
            }

            if (SceneManager.GetActiveScene().name == "Squad Maker")
            {
                PolishSquadMakerScrolling();
            }
        }

        private static void PolishTooltip(Tooltip tooltip)
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
            size.y = Mathf.Max(MinimumTutorialHeight, requiredHeight);
            tooltip.TooltipSize.sizeDelta = size;

            Graphic body = tooltip.TooltipSize.GetComponent<Graphic>();
            if (body != null)
            {
                Outline[] outlines = body.GetComponents<Outline>();
                Outline outer = outlines.Length > 0 ? outlines[0] : body.gameObject.AddComponent<Outline>();
                Outline inner = outlines.Length > 1 ? outlines[1] : body.gameObject.AddComponent<Outline>();
                outer.effectColor = TutorialBorderColor;
                outer.effectDistance = new Vector2(3f, -3f);
                outer.useGraphicAlpha = true;
                inner.effectColor = TutorialBorderColor;
                inner.effectDistance = new Vector2(1.5f, -1.5f);
                inner.useGraphicAlpha = true;
            }

            StyleSequenceButton(tooltip.TooltipSize.Find("Tutorial Sequence Footer/Previous"));
            StyleSequenceButton(tooltip.TooltipSize.Find("Tutorial Sequence Footer/Next"));
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

                TMP_Text[] labels = scrollRect.GetComponentsInChildren<TMP_Text>(true);
                for (int labelIndex = 0; labelIndex < labels.Length; labelIndex++)
                {
                    if (labels[labelIndex].GetComponent<SquadListScrollForwarder>() == null)
                    {
                        labels[labelIndex].gameObject.AddComponent<SquadListScrollForwarder>();
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
