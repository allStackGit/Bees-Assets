using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

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
        public void AuthoredDirectTextIsNormalizedInsideTheClampedTooltipRect()
        {
            GameObject descriptionObject = new GameObject("Description", typeof(RectTransform));
            GameObject contentObject = new GameObject("Contents", typeof(RectTransform));
            RectTransform description = descriptionObject.GetComponent<RectTransform>();
            RectTransform content = contentObject.GetComponent<RectTransform>();

            description.sizeDelta = new Vector2(320f, 120f);
            content.SetParent(description, false);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.zero;
            content.pivot = new Vector2(0.5f, 0.5f);
            content.anchoredPosition = new Vector2(110f, -172.5f);
            content.sizeDelta = new Vector2(200f, 325f);

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
        public void StructuralStartTextLayoutCannotRewriteCanonicalTooltipWidthAfterNormalization()
        {
            GameObject descriptionObject = new GameObject("Start Text", typeof(RectTransform));
            GameObject contentObject = new GameObject("Contents", typeof(RectTransform));
            RectTransform description = descriptionObject.GetComponent<RectTransform>();
            RectTransform content = contentObject.GetComponent<RectTransform>();

            description.sizeDelta = new Vector2(320f, 120f);
            HorizontalLayoutGroup authoredLayout =
                descriptionObject.AddComponent<HorizontalLayoutGroup>();
            authoredLayout.padding = new RectOffset(0, 0, 0, 0);
            authoredLayout.spacing = 0f;
            authoredLayout.childAlignment = TextAnchor.UpperLeft;
            authoredLayout.childForceExpandWidth = true;
            authoredLayout.childForceExpandHeight = true;
            authoredLayout.childControlWidth = false;
            authoredLayout.childControlHeight = false;

            content.SetParent(description, false);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.zero;
            content.pivot = new Vector2(0.5f, 0.5f);
            content.anchoredPosition = new Vector2(110f, -172.5f);
            content.sizeDelta = new Vector2(200f, 325f);

            try
            {
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "DisableDescriptionLayoutWriters",
                    description);
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "NormalizeContentRect",
                    description,
                    content,
                    16f,
                    10f);

                LayoutRebuilder.ForceRebuildLayoutImmediate(description);

                Assert.That(authoredLayout.enabled, Is.False,
                    "Start Text/Test Text carry a structural HorizontalLayoutGroup in the tracked scene; it must be disabled once the object becomes a hover overlay.");
                Assert.That(content.rect.width, Is.EqualTo(304f).Within(0.01f),
                    "A later layout rebuild must not collapse the rendered hover text to a one-character-wide column.");
                Assert.That(content.rect.height, Is.EqualTo(110f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(descriptionObject);
            }
        }
    }
}
