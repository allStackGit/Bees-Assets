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

        [TestCase(350f, 718f, 718f, 150f, 350f)]
        [TestCase(350f, 718f, 710f, 150f, 342f)]
        [TestCase(350f, 718f, 900f, 150f, 532f)]
        [TestCase(350f, 718f, 500f, 150f, 150f)]
        public void DetailsHeightUsesOnlyViewportDeltaFromAuthoredGeometry(
            float referenceDetailsHeight,
            float referenceOwnerHeight,
            float liveOwnerHeight,
            float minimumHeight,
            float expected)
        {
            float result = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateResponsiveDetailsHeight",
                referenceDetailsHeight,
                referenceOwnerHeight,
                liveOwnerHeight,
                minimumHeight);

            Assert.That(result, Is.EqualTo(expected).Within(0.01f));
        }

        [Test]
        public void ReferenceSizedColumnDoesNotConsumeAuthoredSlackIntoDetailsRow()
        {
            float result = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateResponsiveDetailsHeight",
                350f,
                718f,
                718f,
                120f);

            Assert.That(result, Is.EqualTo(350f).Within(0.01f),
                "At the authored height the details row must remain authored-sized; pre-existing vertical slack belongs to the composition, not to the report.");
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

        [Test]
        public void ApplyFitRestoresCollapsedSupplyCapacityRowItself()
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
            Component guard = column.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                RuntimeAssembly.SetField(guard, "_chosenColumn", column);
                RuntimeAssembly.SetField(guard, "_detailsRow", details);
                RuntimeAssembly.SetField(guard, "_supplyRow", supply);
                RuntimeAssembly.SetField(guard, "_levelDetailsText", null);
                RuntimeAssembly.SetField(guard, "_supplyText", null);
                RuntimeAssembly.SetField(guard, "_referenceChosenColumnHeight", 718f);
                RuntimeAssembly.SetField(guard, "_referenceDetailsHeight", 350f);
                RuntimeAssembly.SetField(guard, "_referenceSupplyHeight", 35f);
                RuntimeAssembly.SetField(guard, "_referenceGeometryCaptured", true);

                RuntimeAssembly.Invoke(guard, "ApplyFit");

                Assert.That(supply.rect.height, Is.EqualTo(35f).Within(0.01f),
                    "Reserving 35px in a budget is insufficient if the actual Supply Capacity RectTransform is still only a clipped strip.");
                Assert.That(details.rect.height, Is.EqualTo(350f).Within(0.01f),
                    "Restoring Supply Capacity must not make a reference-sized details panel consume unrelated authored slack.");
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
        }

        [TestCase(0f, -3f, 3f)]
        [TestCase(0f, 0f, 0f)]
        [TestCase(-359f, -360.5f, 1.5f)]
        public void BottomOverflowMeasuresOnlyRenderedContentBelowOwner(
            float ownerBottom,
            float renderedBottom,
            float expected)
        {
            float result = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateBottomOverflow",
                ownerBottom,
                renderedBottom);

            Assert.That(result, Is.EqualTo(expected).Within(0.01f));
        }

        [Test]
        public void DetailsHeightReturnsToAuthoredValueAfterTallViewport()
        {
            System.Type guardType = RuntimeAssembly.GetType(GuardTypeName);

            float tall = (float)RuntimeAssembly.InvokeStatic(
                guardType,
                "CalculateResponsiveDetailsHeight",
                350f,
                718f,
                949f,
                150f);
            float restored = (float)RuntimeAssembly.InvokeStatic(
                guardType,
                "CalculateResponsiveDetailsHeight",
                350f,
                718f,
                718f,
                150f);

            Assert.That(tall, Is.EqualTo(581f).Within(0.01f));
            Assert.That(restored, Is.EqualTo(350f).Within(0.01f),
                "Responsive repair must derive from immutable authored geometry instead of the previous live height.");
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
