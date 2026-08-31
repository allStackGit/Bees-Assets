using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerLevelDetailsFitGuardTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerLevelDetailsFitGuard";

        [TestCase(350f, 718f, 718f, 368f, 350f)]
        [TestCase(350f, 718f, 710f, 368f, 342f)]
        [TestCase(350f, 718f, 900f, 368f, 532f)]
        [TestCase(350f, 718f, 718f, 348f, 370f)]
        [TestCase(350f, 718f, 718f, 388f, 330f)]
        public void DetailsHeightUsesLiveRemainderAndNeverExceedsLiveBudget(
            float referenceDetailsHeight,
            float referenceOwnerHeight,
            float liveOwnerHeight,
            float fixedHeight,
            float expected)
        {
            float result = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateFittingDetailsHeight",
                referenceDetailsHeight,
                referenceOwnerHeight,
                liveOwnerHeight,
                fixedHeight);

            Assert.That(result, Is.EqualTo(expected).Within(0.01f));
        }

        [Test]
        public void ReferenceSizedColumnConsumesResidualSlackIntoFlexibleDetailsRow()
        {
            float result = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateFittingDetailsHeight",
                350f,
                718f,
                718f,
                340f);

            Assert.That(result, Is.EqualTo(378f).Within(0.01f),
                "In level-details mode the report must consume residual live space so Supply Capacity reaches the bottom boundary instead of floating above it.");
        }

        [Test]
        public void SupplyCapacityRowUsesAuthoredOrTextHeightWhicheverIsLarger()
        {
            float protectedHeight = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateProtectedRowHeight",
                35f,
                27f);

            Assert.That(protectedHeight, Is.EqualTo(35f).Within(0.01f));
        }

        [TestCase(-1f, 278f, true, 278f)]
        [TestCase(663f, 278f, true, 278f)]
        [TestCase(278f, 298f, false, 278f)]
        [TestCase(278f, 509f, false, 278f)]
        public void LevelDetailsSemanticListHeightIsCapturedOnEntryButNotRecapturedFromLayout(
            float capturedHeight,
            float currentHeight,
            bool enteringDetails,
            float expected)
        {
            float result = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateStableLevelDetailsListHeight",
                capturedHeight,
                currentHeight,
                enteringDetails);

            Assert.That(result, Is.EqualTo(expected).Within(0.01f));
        }

        [Test]
        public void FixedHeightBudgetIgnoresOverlayRowsAndProtectsCollapsedSupplyRow()
        {
            RectTransform column = CreateRect("Chosen Squads Column", null, new Vector2(222f, 718f));
            VerticalLayoutGroup layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            CreateRect("Chosen Squads Heading", column, new Vector2(222f, 30f));
            CreateRect("Chosen Squad List", column, new Vector2(222f, 278f));
            CreateRect("Level Title", column, new Vector2(222f, 25f));
            RectTransform details = CreateRect("Details Container", column, new Vector2(222f, 350f));
            RectTransform supply = CreateRect("Supply Capacity", column, new Vector2(222f, 2f));

            RectTransform hover = CreateRect("Start Text", column, new Vector2(222f, 450f));
            LayoutElement hoverLayout = hover.gameObject.AddComponent<LayoutElement>();
            hoverLayout.ignoreLayout = true;

            try
            {
                float fixedHeight = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateOtherActiveRowHeight",
                    column,
                    details,
                    supply,
                    35f);

                Assert.That(fixedHeight, Is.EqualTo(368f).Within(0.01f),
                    "The budget must use the protected Supply Capacity height and ignore hover-only overlays.");
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
        }

        [Test]
        public void ApplyFitRestoresCollapsedSupplyCapacityRowAndUsesRemainingHeight()
        {
            RectTransform column = CreateRect("Chosen Squads Column", null, new Vector2(222f, 718f));
            ConfigureColumnLayout(column);

            CreateRect("Chosen Squads Heading", column, new Vector2(222f, 30f));
            RectTransform chosenList = CreateRect("Chosen Squad List", column, new Vector2(222f, 278f));
            CreateRect("Level Title", column, new Vector2(222f, 25f));
            RectTransform details = CreateRect("Details Container", column, new Vector2(222f, 350f));
            RectTransform supply = CreateRect("Supply Capacity", column, new Vector2(222f, 2f));
            Component guard = column.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                ConfigureGuardForFixture(
                    guard,
                    column,
                    chosenList,
                    details,
                    supply,
                    718f,
                    350f,
                    35f);
                RuntimeAssembly.Invoke(guard, "ApplyFit");

                Assert.That(supply.rect.height, Is.EqualTo(35f).Within(0.01f),
                    "The real Supply Capacity RectTransform must be restored, not merely reserved in a calculation.");
                Assert.That(chosenList.rect.height, Is.EqualTo(278f).Within(0.01f));
                Assert.That(details.rect.height, Is.EqualTo(350f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
        }

        [Test]
        public void InflatedChosenListReturnsToLevelDetailsSemanticHeightBeforeReportIsFitted()
        {
            RectTransform column = CreateRect("Chosen Squads Column", null, new Vector2(222f, 718f));
            ConfigureColumnLayout(column);

            CreateRect("Chosen Squads Heading", column, new Vector2(222f, 30f));
            RectTransform chosenList = CreateRect("Chosen Squad List", column, new Vector2(222f, 278f));
            CreateRect("Level Title", column, new Vector2(222f, 25f));
            RectTransform details = CreateRect("Details Container", column, new Vector2(222f, 350f));
            RectTransform supply = CreateRect("Supply Capacity", column, new Vector2(222f, 35f));
            Component guard = column.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                ConfigureGuardForFixture(
                    guard,
                    column,
                    chosenList,
                    details,
                    supply,
                    718f,
                    350f,
                    35f);

                // First pass observes the 278px height deliberately selected by ToggleLevelDetails.
                RuntimeAssembly.Invoke(guard, "ApplyFit");
                Assert.That(chosenList.rect.height, Is.EqualTo(278f).Within(0.01f));
                Assert.That(details.rect.height, Is.EqualTo(350f).Within(0.01f));

                // Reproduce the real failure family: another responsive/layout pass makes the list
                // taller while level-details mode remains active. This is presentation drift, not a
                // new semantic state, so it must not steal space from the selected-level report.
                chosenList.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 298f);
                RuntimeAssembly.Invoke(guard, "ApplyFit");

                Assert.That(chosenList.rect.height, Is.EqualTo(278f).Within(0.01f),
                    "A transiently enlarged chosen-list row must not become the new level-details semantic height.");
                Assert.That(details.rect.height, Is.EqualTo(350f).Within(0.01f),
                    "Restoring the list owner should preserve the live report budget instead of hiding level details such as Supply Capacity.");

                Bounds supplyBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(column, supply);
                Assert.That(supplyBounds.min.y, Is.GreaterThanOrEqualTo(column.rect.yMin - 0.01f));
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
        }

        [Test]
        public void UnrelatedTallerStructuralNeighborStillReducesFlexibleReport()
        {
            RectTransform column = CreateRect("Chosen Squads Column", null, new Vector2(222f, 718f));
            ConfigureColumnLayout(column);

            CreateRect("Chosen Squads Heading", column, new Vector2(222f, 30f));
            RectTransform chosenList = CreateRect("Chosen Squad List", column, new Vector2(222f, 278f));
            CreateRect("Level Title", column, new Vector2(222f, 25f));
            CreateRect("Unexpected Fixed Row", column, new Vector2(222f, 20f));
            RectTransform details = CreateRect("Details Container", column, new Vector2(222f, 350f));
            RectTransform supply = CreateRect("Supply Capacity", column, new Vector2(222f, 35f));
            Component guard = column.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                ConfigureGuardForFixture(
                    guard,
                    column,
                    chosenList,
                    details,
                    supply,
                    718f,
                    350f,
                    35f);
                RuntimeAssembly.Invoke(guard, "ApplyFit");

                Assert.That(chosenList.rect.height, Is.EqualTo(278f).Within(0.01f));
                Assert.That(details.rect.height, Is.EqualTo(330f).Within(0.01f),
                    "Only genuinely separate structural height should be paid for by the flexible report.");
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
        }

        [Test]
        public void DetailsHeightTracksNeighborAndViewportChangesWithoutDrift()
        {
            System.Type guardType = RuntimeAssembly.GetType(GuardTypeName);

            float reference = Calculate(guardType, 718f, 368f);
            float tall = Calculate(guardType, 949f, 368f);
            float tallerNeighbor = Calculate(guardType, 718f, 388f);
            float restored = Calculate(guardType, 718f, 368f);

            Assert.That(reference, Is.EqualTo(350f).Within(0.01f));
            Assert.That(tall, Is.EqualTo(581f).Within(0.01f));
            Assert.That(tallerNeighbor, Is.EqualTo(330f).Within(0.01f));
            Assert.That(restored, Is.EqualTo(350f).Within(0.01f),
                "Every pass must derive from the live owner and fixed-row budget rather than the previous details height.");
        }

        private static float Calculate(System.Type guardType, float liveOwnerHeight, float fixedHeight)
        {
            return (float)RuntimeAssembly.InvokeStatic(
                guardType,
                "CalculateFittingDetailsHeight",
                350f,
                718f,
                liveOwnerHeight,
                fixedHeight);
        }

        private static void ConfigureColumnLayout(RectTransform column)
        {
            VerticalLayoutGroup layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }

        private static void ConfigureGuardForFixture(
            Component guard,
            RectTransform column,
            RectTransform chosenList,
            RectTransform details,
            RectTransform supply,
            float referenceOwnerHeight,
            float referenceDetailsHeight,
            float referenceSupplyHeight)
        {
            RuntimeAssembly.SetField(guard, "_chosenColumn", column);
            RuntimeAssembly.SetField(guard, "_chosenListRow", chosenList);
            RuntimeAssembly.SetField(guard, "_detailsRow", details);
            RuntimeAssembly.SetField(guard, "_supplyRow", supply);
            RuntimeAssembly.SetField(guard, "_supplyText", null);
            RuntimeAssembly.SetField(guard, "_referenceChosenColumnHeight", referenceOwnerHeight);
            RuntimeAssembly.SetField(guard, "_referenceDetailsHeight", referenceDetailsHeight);
            RuntimeAssembly.SetField(guard, "_referenceSupplyHeight", referenceSupplyHeight);
            RuntimeAssembly.SetField(guard, "_referenceGeometryCaptured", true);
        }

        private static RectTransform CreateRect(string name, RectTransform parent, Vector2 size)
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
