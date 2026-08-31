using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerFooterControlHeightTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerResponsiveLayoutGuard";

        [Test]
        public void FooterHeightUsesBottomAnchoredControlTopInsteadOfAuthoredBlankSpace()
        {
            RectTransform footer = CreateRect("Footer", null, new Vector2(1366f, 51f));
            RectTransform start = CreateBottomControl("START", footer, new Vector2(100f, 30f), 0f);
            RectTransform test = CreateBottomControl("TEST", footer, new Vector2(100f, 30f), 0f);

            try
            {
                float height = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateFooterControlHeight",
                    footer,
                    new[] { start, test });

                Assert.That(height, Is.EqualTo(30f).Within(0.01f),
                    "A 51px authored footer containing 30px bottom-aligned controls must not preserve the unused 21px band above those controls.");
            }
            finally
            {
                Object.DestroyImmediate(footer.gameObject);
            }
        }

        [Test]
        public void FooterHeightPreservesRealBottomInsetButNotUnusedTopSpace()
        {
            RectTransform footer = CreateRect("Footer", null, new Vector2(1366f, 51f));
            RectTransform start = CreateBottomControl("START", footer, new Vector2(100f, 30f), 2f);

            try
            {
                float height = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateFooterControlHeight",
                    footer,
                    new[] { start });

                Assert.That(height, Is.EqualTo(32f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(footer.gameObject);
            }
        }

        [Test]
        public void FooterWithoutResolvedControlsKeepsAuthoredHeight()
        {
            RectTransform footer = CreateRect("Footer", null, new Vector2(1366f, 51f));

            try
            {
                float height = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateFooterControlHeight",
                    footer,
                    new RectTransform[0]);

                Assert.That(height, Is.EqualTo(51f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(footer.gameObject);
            }
        }

        private static RectTransform CreateBottomControl(
            string name,
            RectTransform parent,
            Vector2 size,
            float bottomInset)
        {
            RectTransform rect = CreateRect(name, parent, size);
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-5f, bottomInset);
            return rect;
        }

        private static RectTransform CreateRect(
            string name,
            RectTransform parent,
            Vector2 size)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            if (parent != null)
            {
                rect.SetParent(parent, false);
            }

            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            return rect;
        }
    }
}
