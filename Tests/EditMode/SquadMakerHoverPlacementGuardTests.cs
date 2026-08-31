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
        public void StartAndTestVisibleTextShareTheChosenColumnCenterDespiteDifferentGlyphBounds()
        {
            Rect overlay = new Rect(-683f, -384f, 1366f, 768f);
            Rect chosenColumn = new Rect(453f, -333f, 230f, 666f);

            // The two descriptions have different paragraph lengths and therefore different rendered
            // glyph envelopes inside their padded outer RectTransforms. Centering outer rects would
            // visibly misalign the paragraphs even if the RectTransforms themselves had the same x.
            Rect startVisibleBounds = new Rect(-70f, 5f, 115f, 42f);
            Rect testVisibleBounds = new Rect(-76f, 5f, 141f, 84f);

            float startX = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateColumnCenteredHoverX",
                chosenColumn,
                startVisibleBounds,
                overlay,
                8f);
            float testX = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateColumnCenteredHoverX",
                chosenColumn,
                testVisibleBounds,
                overlay,
                8f);

            Assert.That(startX + startVisibleBounds.center.x,
                Is.EqualTo(chosenColumn.center.x).Within(0.01f),
                "START's rendered paragraph must be centered in the Chosen Squads column, not on the START button or the padded outer tooltip.");
            Assert.That(testX + testVisibleBounds.center.x,
                Is.EqualTo(chosenColumn.center.x).Within(0.01f),
                "TEST's rendered paragraph must use the same Chosen Squads-column center even though its glyph bounds differ from START's.");

            AssertVisibleBoundsInsideOverlay(startX, startVisibleBounds, overlay, 8f);
            AssertVisibleBoundsInsideOverlay(testX, testVisibleBounds, overlay, 8f);
        }

        [Test]
        public void ColumnCenteredPlacementClampsOnlyRenderedContentAtCanvasEdge()
        {
            Rect overlay = new Rect(-100f, -100f, 200f, 200f);
            Rect chosenColumn = new Rect(60f, -80f, 40f, 160f);
            Rect visibleBounds = new Rect(-80f, 0f, 130f, 40f);

            float x = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateColumnCenteredHoverX",
                chosenColumn,
                visibleBounds,
                overlay,
                8f);

            Assert.That(x, Is.EqualTo(42f).Within(0.01f),
                "When the column center would push visible text off-screen, clamp the rendered glyph edge to the canvas margin without using unused outer-tooltip padding.");
            AssertVisibleBoundsInsideOverlay(x, visibleBounds, overlay, 8f);
        }

        [Test]
        public void ColumnCenteredPlacementFallsBackToCenteredVisibleContentWhenContentCannotFitSafeRegion()
        {
            Rect overlay = new Rect(-100f, -100f, 200f, 200f);
            Rect chosenColumn = new Rect(60f, -80f, 40f, 160f);
            Rect visibleBounds = new Rect(-120f, 0f, 240f, 40f);

            float x = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateColumnCenteredHoverX",
                chosenColumn,
                visibleBounds,
                overlay,
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
