using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerHoverDescriptionSizeGuardTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerHoverDescriptionSizeGuard";

        [Test]
        public void LongHoverDescriptionUsesCompactTooltipWidthInsteadOfStructuralRowWidth()
        {
            float width = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateCompactWidth",
                520f,
                320f,
                160f,
                16f);

            Assert.That(width, Is.EqualTo(320f).Within(0.01f));
        }

        [Test]
        public void ShortHoverDescriptionStillHasReadableMinimumWidth()
        {
            float width = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateCompactWidth",
                60f,
                320f,
                160f,
                16f);

            Assert.That(width, Is.EqualTo(160f).Within(0.01f));
        }

        [Test]
        public void HoverDescriptionHeightComesFromWrappedTextNotInheritedLayoutRow()
        {
            float height = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateCompactHeight",
                36f,
                24f,
                120f,
                10f);

            Assert.That(height, Is.EqualTo(46f).Within(0.01f),
                "A description that inherited a 400-500px structural row must collapse to its actual text height before overlay positioning.");
        }

        [Test]
        public void ExtremelyLongHoverTextIsCappedToAReasonableOverlayHeight()
        {
            float height = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateCompactHeight",
                300f,
                24f,
                120f,
                10f);

            Assert.That(height, Is.EqualTo(120f).Within(0.01f));
        }

        [Test]
        public void NarrowCanvasConstrainsTooltipWidthWithoutViolatingAvailableSpace()
        {
            float width = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateCompactWidth",
                400f,
                140f,
                160f,
                16f);

            Assert.That(width, Is.EqualTo(140f).Within(0.01f));
        }

        [Test]
        public void OffsetNestedTextIsNormalizedInsideTheClampedTooltipRect()
        {
            GameObject descriptionObject = new GameObject("Description", typeof(RectTransform));
            GameObject contentObject = new GameObject("Text", typeof(RectTransform));
            RectTransform description = descriptionObject.GetComponent<RectTransform>();
            RectTransform content = contentObject.GetComponent<RectTransform>();

            description.sizeDelta = new Vector2(320f, 120f);
            content.SetParent(description, false);
            content.anchorMin = new Vector2(1f, 0.5f);
            content.anchorMax = new Vector2(1f, 0.5f);
            content.pivot = new Vector2(0f, 0.5f);
            content.anchoredPosition = new Vector2(100f, 0f);
            content.sizeDelta = new Vector2(300f, 100f);

            try
            {
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "NormalizeContentRect",
                    description,
                    content,
                    16f,
                    10f);

                Assert.That(content.anchorMin, Is.EqualTo(Vector2.zero));
                Assert.That(content.anchorMax, Is.EqualTo(Vector2.one));
                Assert.That(content.offsetMin.x, Is.EqualTo(8f).Within(0.01f));
                Assert.That(content.offsetMin.y, Is.EqualTo(5f).Within(0.01f));
                Assert.That(content.offsetMax.x, Is.EqualTo(-8f).Within(0.01f));
                Assert.That(content.offsetMax.y, Is.EqualTo(-5f).Within(0.01f));

                Bounds contentBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                    description,
                    content);
                Assert.That(contentBounds.min.x,
                    Is.GreaterThanOrEqualTo(description.rect.xMin + 7.99f));
                Assert.That(contentBounds.max.x,
                    Is.LessThanOrEqualTo(description.rect.xMax - 7.99f));
            }
            finally
            {
                Object.DestroyImmediate(descriptionObject);
            }
        }

        [Test]
        public void NarrowIntermediateWrapperCannotCollapseTooltipTextToOneCharacterPerLine()
        {
            GameObject descriptionObject = new GameObject("Description", typeof(RectTransform));
            GameObject wrapperObject = new GameObject("Narrow Wrapper", typeof(RectTransform));
            GameObject contentObject = new GameObject("Text", typeof(RectTransform));
            RectTransform description = descriptionObject.GetComponent<RectTransform>();
            RectTransform wrapper = wrapperObject.GetComponent<RectTransform>();
            RectTransform content = contentObject.GetComponent<RectTransform>();

            description.sizeDelta = new Vector2(320f, 120f);
            wrapper.SetParent(description, false);
            wrapper.anchorMin = new Vector2(0.5f, 0.5f);
            wrapper.anchorMax = new Vector2(0.5f, 0.5f);
            wrapper.pivot = new Vector2(0.5f, 0.5f);
            wrapper.sizeDelta = new Vector2(8f, 100f);

            content.SetParent(wrapper, false);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            try
            {
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "NormalizeContentRect",
                    description,
                    content,
                    16f,
                    10f);

                Assert.That(wrapper.anchorMin, Is.EqualTo(Vector2.zero));
                Assert.That(wrapper.anchorMax, Is.EqualTo(Vector2.one));
                Assert.That(wrapper.offsetMin, Is.EqualTo(Vector2.zero));
                Assert.That(wrapper.offsetMax, Is.EqualTo(Vector2.zero));

                Bounds wrapperBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                    description,
                    wrapper);
                Bounds contentBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                    description,
                    content);

                Assert.That(wrapperBounds.size.x, Is.EqualTo(320f).Within(0.01f),
                    "The intermediate wrapper must no longer impose its authored 8px width on the tooltip text.");
                Assert.That(contentBounds.size.x, Is.EqualTo(304f).Within(0.01f),
                    "The final text rect should receive only the intended 16px total horizontal padding.");
            }
            finally
            {
                Object.DestroyImmediate(descriptionObject);
            }
        }
    }
}
