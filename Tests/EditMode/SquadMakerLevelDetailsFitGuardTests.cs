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

        [TestCase(718f, 368f, 350f, 150f, 350f)]
        [TestCase(710f, 368f, 350f, 150f, 342f)]
        [TestCase(900f, 368f, 350f, 150f, 350f)]
        [TestCase(500f, 368f, 350f, 150f, 150f)]
        public void DetailsHeightUsesOnlyAvailableSlackWithoutClippingRequiredText(
            float ownerHeight,
            float fixedHeight,
            float authoredHeight,
            float minimumHeight,
            float expected)
        {
            float result = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateFittingDetailsHeight",
                ownerHeight,
                fixedHeight,
                authoredHeight,
                minimumHeight);

            Assert.That(result, Is.EqualTo(expected).Within(0.01f));
        }

        [Test]
        public void LevelDetailsRowAbsorbsChosenColumnDeficitBeforeBottomRowsAreClipped()
        {
            RectTransform column = CreateRect("Chosen Squads Column", null, new Vector2(222f, 710f));
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
            CreateRect("Supply Capacity", column, new Vector2(222f, 35f));

            RectTransform hover = CreateRect("Start Text", column, new Vector2(222f, 450f));
            LayoutElement hoverLayout = hover.gameObject.AddComponent<LayoutElement>();
            hoverLayout.ignoreLayout = true;

            Component guard = column.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                float fixedHeight = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateOtherActiveRowHeight",
                    column,
                    details);
                Assert.That(fixedHeight, Is.EqualTo(368f).Within(0.01f),
                    "Hover-only descriptions must not consume the right-column height budget.");

                RuntimeAssembly.SetField(guard, "_chosenColumn", column);
                RuntimeAssembly.SetField(guard, "_detailsRow", details);
                RuntimeAssembly.SetField(guard, "_levelDetailsText", null);
                RuntimeAssembly.SetField(guard, "_authoredDetailsHeight", 350f);
                RuntimeAssembly.Invoke(guard, "ApplyFit");

                Assert.That(details.rect.height, Is.EqualTo(342f).Within(0.01f),
                    "The large details container should give up only its unused slack so the supply-capacity row stays visible.");
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
        }

        [Test]
        public void DetailsHeightReturnsToAuthoredSizeWhenViewportRoomReturns()
        {
            RectTransform column = CreateRect("Chosen Squads Column", null, new Vector2(222f, 710f));
            VerticalLayoutGroup layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            CreateRect("Chosen Squads Heading", column, new Vector2(222f, 30f));
            CreateRect("Chosen Squad List", column, new Vector2(222f, 278f));
            CreateRect("Level Title", column, new Vector2(222f, 25f));
            RectTransform details = CreateRect("Details Container", column, new Vector2(222f, 350f));
            CreateRect("Supply Capacity", column, new Vector2(222f, 35f));
            Component guard = column.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                RuntimeAssembly.SetField(guard, "_chosenColumn", column);
                RuntimeAssembly.SetField(guard, "_detailsRow", details);
                RuntimeAssembly.SetField(guard, "_levelDetailsText", null);
                RuntimeAssembly.SetField(guard, "_authoredDetailsHeight", 350f);

                RuntimeAssembly.Invoke(guard, "ApplyFit");
                Assert.That(details.rect.height, Is.EqualTo(342f).Within(0.01f));

                column.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 718f);
                RuntimeAssembly.Invoke(guard, "ApplyFit");
                Assert.That(details.rect.height, Is.EqualTo(350f).Within(0.01f),
                    "Responsive repair must remain reversible instead of making the shortened details row the next baseline.");
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
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
