using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerSerializedLayoutContractTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.SquadMakerResponsiveLayoutGuard";
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);

        [Test]
        public void RepeatedAspectChangesRebuildTheAuthoredSquadMakerHierarchyWithoutDrift()
        {
            RectTransform canvas = CreateRect("Canvas", null, ReferenceResolution);
            RectTransform mainPanel = CreateRect("MainPanel", canvas, Vector2.zero);
            mainPanel.anchorMin = Vector2.zero;
            mainPanel.anchorMax = Vector2.one;
            mainPanel.anchoredPosition = Vector2.zero;
            mainPanel.sizeDelta = Vector2.zero;

            VerticalLayoutGroup panelLayout = mainPanel.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.childControlWidth = false;
            panelLayout.childControlHeight = false;
            panelLayout.childForceExpandWidth = false;
            panelLayout.childForceExpandHeight = false;

            RectTransform mainContainer = CreateRect("Main Container", mainPanel, new Vector2(1366f, 718f));
            HorizontalLayoutGroup mainLayout = mainContainer.gameObject.AddComponent<HorizontalLayoutGroup>();
            mainLayout.childControlWidth = false;
            mainLayout.childControlHeight = false;
            mainLayout.childForceExpandWidth = false;
            mainLayout.childForceExpandHeight = false;

            RectTransform footer = CreateRect("Footer", mainPanel, new Vector2(1366f, 51f));
            RectTransform shipSelector = CreateRect("Ship Selector Column", mainContainer, new Vector2(262f, 718f));
            RectTransform squadMaker = CreateRect("Squad Maker Column", mainContainer, new Vector2(620f, 718f));
            RectTransform squads = CreateRect("Squads Column", mainContainer, new Vector2(484f, 718f));

            HorizontalLayoutGroup squadsLayout = squads.gameObject.AddComponent<HorizontalLayoutGroup>();
            squadsLayout.childControlWidth = false;
            squadsLayout.childControlHeight = false;
            squadsLayout.childForceExpandWidth = false;
            squadsLayout.childForceExpandHeight = false;

            RectTransform savedSquads = CreateRect("Saved Squads Column", squads, new Vector2(262f, 718f));
            RectTransform chosenSquads = CreateRect("Chosen Squads Column", squads, new Vector2(222f, 718f));

            Component guard = canvas.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                RuntimeAssembly.SetField(guard, "_canvasRect", canvas);
                RuntimeAssembly.Invoke(guard, "CaptureDirectReferenceBranches");
                RuntimeAssembly.SetField(guard, "_referenceGeometryCaptured", true);

                ApplyAndAssert(
                    guard,
                    canvas,
                    new Vector2(2000f, 768f),
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squads,
                    savedSquads,
                    chosenSquads,
                    new Vector2(2000f, 718f),
                    new Vector2(2000f, 51f),
                    new Vector2(262f, 718f),
                    new Vector2(1254f, 718f),
                    new Vector2(484f, 718f),
                    new Vector2(262f, 718f),
                    new Vector2(222f, 718f));

                ApplyAndAssert(
                    guard,
                    canvas,
                    new Vector2(1366f, 1000f),
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squads,
                    savedSquads,
                    chosenSquads,
                    new Vector2(1366f, 950f),
                    new Vector2(1366f, 51f),
                    new Vector2(262f, 950f),
                    new Vector2(620f, 950f),
                    new Vector2(484f, 950f),
                    new Vector2(262f, 950f),
                    new Vector2(222f, 950f));

                // Revisit a prior aspect after a different allocation. This catches stateful drift.
                ApplyAndAssert(
                    guard,
                    canvas,
                    new Vector2(2000f, 768f),
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squads,
                    savedSquads,
                    chosenSquads,
                    new Vector2(2000f, 718f),
                    new Vector2(2000f, 51f),
                    new Vector2(262f, 718f),
                    new Vector2(1254f, 718f),
                    new Vector2(484f, 718f),
                    new Vector2(262f, 718f),
                    new Vector2(222f, 718f));

                ApplyAndAssert(
                    guard,
                    canvas,
                    ReferenceResolution,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squads,
                    savedSquads,
                    chosenSquads,
                    new Vector2(1366f, 718f),
                    new Vector2(1366f, 51f),
                    new Vector2(262f, 718f),
                    new Vector2(620f, 718f),
                    new Vector2(484f, 718f),
                    new Vector2(262f, 718f),
                    new Vector2(222f, 718f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        private static void ApplyAndAssert(
            Component guard,
            RectTransform canvas,
            Vector2 canvasSize,
            RectTransform mainContainer,
            RectTransform footer,
            RectTransform shipSelector,
            RectTransform squadMaker,
            RectTransform squads,
            RectTransform savedSquads,
            RectTransform chosenSquads,
            Vector2 expectedMainContainer,
            Vector2 expectedFooter,
            Vector2 expectedShipSelector,
            Vector2 expectedSquadMaker,
            Vector2 expectedSquads,
            Vector2 expectedSavedSquads,
            Vector2 expectedChosenSquads)
        {
            canvas.sizeDelta = canvasSize;
            RuntimeAssembly.Invoke(guard, "ApplyViewportFill");

            AssertSize(mainContainer, expectedMainContainer);
            AssertSize(footer, expectedFooter);
            AssertSize(shipSelector, expectedShipSelector);
            AssertSize(squadMaker, expectedSquadMaker);
            AssertSize(squads, expectedSquads);
            AssertSize(savedSquads, expectedSavedSquads);
            AssertSize(chosenSquads, expectedChosenSquads);
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

        private static void AssertSize(RectTransform rect, Vector2 expected)
        {
            Assert.That(rect.rect.width, Is.EqualTo(expected.x).Within(0.01f), rect.name + " width");
            Assert.That(rect.rect.height, Is.EqualTo(expected.y).Within(0.01f), rect.name + " height");
        }
    }
}
