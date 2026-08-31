using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerSupplyCapacityPresentationGuardTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerSupplyCapacityPresentationGuard";

        [Test]
        public void SupplyCapacityTextFillsWarningBackgroundAndCentersGlyphs()
        {
            GameObject backgroundObject = new GameObject("Supply Capacity Background", typeof(RectTransform));
            RectTransform background = backgroundObject.GetComponent<RectTransform>();
            background.sizeDelta = new Vector2(270f, 35f);

            GameObject labelObject = new GameObject("Supply Capacity Label", typeof(RectTransform));
            RectTransform label = labelObject.GetComponent<RectTransform>();
            label.SetParent(background, false);
            label.anchorMin = new Vector2(0.5f, 0.5f);
            label.anchorMax = new Vector2(0.5f, 0.5f);
            label.pivot = new Vector2(0f, 1f);
            label.anchoredPosition = new Vector2(13f, -5f);
            label.sizeDelta = new Vector2(220f, 22f);

            GameObject textObject = new GameObject(
                "Text",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(label, false);
            textRect.anchorMin = new Vector2(0.5f, 0.5f);
            textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.pivot = new Vector2(0f, 1f);
            textRect.anchoredPosition = new Vector2(-9f, 4f);
            textRect.sizeDelta = new Vector2(180f, 18f);

            TMP_Text text = textObject.GetComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.TopLeft;
            text.margin = new Vector4(4f, 2f, 7f, 5f);

            try
            {
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CenterPresentation",
                    background,
                    label,
                    text);

                AssertStretched(label);
                AssertStretched(textRect);
                Assert.That(text.alignment, Is.EqualTo(TextAlignmentOptions.Center));
                Assert.That(text.margin, Is.EqualTo(Vector4.zero));
            }
            finally
            {
                Object.DestroyImmediate(backgroundObject);
            }
        }

        private static void AssertStretched(RectTransform rect)
        {
            Assert.That(rect.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(rect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(rect.pivot, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(rect.anchoredPosition, Is.EqualTo(Vector2.zero));
            Assert.That(rect.sizeDelta, Is.EqualTo(Vector2.zero));
            Assert.That(rect.localScale, Is.EqualTo(Vector3.one));
        }
    }
}
