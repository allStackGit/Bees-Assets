using NUnit.Framework;

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
    }
}
