using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerHoverPlacementGuardTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerHoverPlacementGuard";

        [Test]
        public void RealFooterButtonPairBiasesStartLeftAndTestRightUsingVisibleBoundsForEdgeSafety()
        {
            Rect overlay = new Rect(-683f, -384f, 1366f, 768f);
            Rect startButton = new Rect(488f, -380f, 85f, 30f);
            Rect testButton = new Rect(583f, -380f, 85f, 30f);
            const float descriptionWidth = 160f;

            // The canonical tooltip has horizontal padding and TMP glyphs do not fill every unit of
            // its outer RectTransform. This local glyph envelope is deliberately narrower than the
            // 160-unit outer tooltip so edge protection models what is actually visible.
            Rect visibleLocalBounds = new Rect(-70f, 5f, 115f, 55f);

            float startX = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateDirectionalHoverX",
                startButton,
                descriptionWidth,
                visibleLocalBounds,
                overlay,
                -1,
                8f);
            float testX = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateDirectionalHoverX",
                testButton,
                descriptionWidth,
                visibleLocalBounds,
                overlay,
                1,
                8f);

            Assert.That(startX, Is.EqualTo(493f).Within(0.01f));
            Assert.That(startX, Is.LessThan(startButton.center.x),
                "START help must expand toward the left rather than remain centered on START.");

            // The old outer-rect clamp capped this 160-unit TEST tooltip at x=595. Using the actual
            // rendered text edge allows it to sit farther right while the visible glyphs remain safe.
            Assert.That(testX, Is.EqualTo(630f).Within(0.01f));
            Assert.That(testX, Is.GreaterThan(595f),
                "TEST help must no longer be pulled left by unused outer-tooltip width.");
            Assert.That(testX, Is.GreaterThan(testButton.center.x),
                "TEST help must expand toward the right from the TEST button.");

            AssertVisibleBoundsInsideOverlay(startX, visibleLocalBounds, overlay, 8f);
            AssertVisibleBoundsInsideOverlay(testX, visibleLocalBounds, overlay, 8f);
        }

        [Test]
        public void DirectionalPlacementFallsBackToCenteredVisibleContentWhenContentCannotFitSafeRegion()
        {
            Rect overlay = new Rect(-100f, -100f, 200f, 200f);
            Rect button = new Rect(50f, -80f, 40f, 30f);
            Rect visibleLocalBounds = new Rect(-120f, 0f, 240f, 40f);

            float x = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateDirectionalHoverX",
                button,
                260f,
                visibleLocalBounds,
                overlay,
                1,
                8f);

            Assert.That(x, Is.EqualTo(0f).Within(0.01f),
                "If the rendered content itself cannot fit, centering minimizes symmetric overflow rather than favoring one edge.");
        }

        private static void AssertVisibleBoundsInsideOverlay(
            float x,
            Rect visibleLocalBounds,
            Rect overlay,
            float margin)
        {
            Assert.That(x + visibleLocalBounds.xMin,
                Is.GreaterThanOrEqualTo(overlay.xMin + margin - 0.001f));
            Assert.That(x + visibleLocalBounds.xMax,
                Is.LessThanOrEqualTo(overlay.xMax - margin + 0.001f));
        }
    }
}
