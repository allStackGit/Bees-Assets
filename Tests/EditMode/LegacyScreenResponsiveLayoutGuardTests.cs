using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class LegacyScreenResponsiveLayoutGuardTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.LegacyScreenResponsiveLayoutGuard";

        [Test]
        public void MainMenuInteractiveBranchDoesNotExpandBeyondReferenceFrameOnWideCanvas()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(2000f, 900f));
            RectTransform background = CreateRect("Starfield", canvas, new Vector2(2000f, 900f));
            RectTransform menuRoot = CreateRect("MainPanel", canvas, new Vector2(1366f, 668f));
            Vector2 backgroundSize = background.sizeDelta;

            AddMainMenuControls(menuRoot);

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ExpandMainMenuInteractiveRoot",
                    canvas);

                Assert.That(changed, Is.False,
                    "An already-authored reference-sized menu should remain unchanged when the viewport only adds surplus space.");
                AssertVector(menuRoot.anchorMin, new Vector2(0.5f, 0.5f));
                AssertVector(menuRoot.anchorMax, new Vector2(0.5f, 0.5f));
                AssertVector(menuRoot.sizeDelta, new Vector2(1366f, 668f),
                    "Ultrawide surplus belongs to the surrounding starfield, not the green menu frame.");
                AssertVector(background.sizeDelta, backgroundSize,
                    "The starfield/background sibling is not owned by the menu frame repair.");
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void MainMenuInteractiveBranchShrinksUniformlyOnTallCanvasAndRestoresAtReferenceCapacity()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(900f, 2000f));
            RectTransform menuRoot = CreateRect("MainPanel", canvas, new Vector2(1366f, 668f));
            AddMainMenuControls(menuRoot);

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ExpandMainMenuInteractiveRoot",
                    canvas);

                float expectedTallHeight = 668f * 900f / 1366f;
                Assert.That(changed, Is.True);
                AssertVector(menuRoot.anchorMin, new Vector2(0.5f, 0.5f));
                AssertVector(menuRoot.anchorMax, new Vector2(0.5f, 0.5f));
                AssertVector(menuRoot.anchoredPosition, Vector2.zero);
                AssertVector(menuRoot.sizeDelta, new Vector2(900f, expectedTallHeight),
                    "A tall/narrow viewport should scale the authored menu frame uniformly rather than stretching it vertically.");

                canvas.sizeDelta = new Vector2(2000f, 900f);
                changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ExpandMainMenuInteractiveRoot",
                    canvas);

                Assert.That(changed, Is.True,
                    "The periodic repair must be able to restore the authored size after a resolution/aspect-ratio change.");
                AssertVector(menuRoot.sizeDelta, new Vector2(1366f, 668f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void NestedStructuralLayoutFillsItsAllocatedCrossAxis()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(2000f, 900f));
            RectTransform region = CreateRect("Squad Presets Region", canvas, new Vector2(1000f, 800f));
            VerticalLayoutGroup layout = region.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;

            RectTransform body = CreateRect("Body", region, new Vector2(800f, 700f));
            RectTransform toolbar = CreateRect("Toolbar", region, new Vector2(800f, 100f));

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "RepairNestedStructuralLayouts",
                    canvas,
                    canvas,
                    0);

                Assert.That(changed, Is.True);
                Assert.That(body.rect.width, Is.EqualTo(1000f).Within(0.01f));
                Assert.That(toolbar.rect.width, Is.EqualTo(1000f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void UltrawideMultiColumnLayoutGivesSurplusToUniquelyDominantRegion()
        {
            RectTransform row = CreateRect("Squad Maker Columns", null, new Vector2(2000f, 800f));
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;

            RectTransform inventory = CreateRect("Inventory", row, new Vector2(250f, 800f));
            RectTransform workArea = CreateRect("Squad Presets", row, new Vector2(900f, 800f));
            RectTransform squads = CreateRect("Squads", row, new Vector2(250f, 800f));

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "FitDominantStructuralHorizontalChild",
                    row);

                Assert.That(changed, Is.True,
                    "A clear work-area column should absorb ultrawide surplus even when it starts below the generic 50% dominance threshold.");
                Assert.That(inventory.rect.width, Is.EqualTo(250f).Within(0.01f));
                Assert.That(workArea.rect.width, Is.EqualTo(1500f).Within(0.01f));
                Assert.That(squads.rect.width, Is.EqualTo(250f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(row.gameObject);
            }
        }

        [Test]
        public void SmallLocalButtonRowIsNotTreatedAsScreenStructure()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(2000f, 900f));
            RectTransform row = CreateRect("Save Buttons", canvas, new Vector2(600f, 60f));
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;

            RectTransform buttonA = CreateRect("Save", row, new Vector2(250f, 50f));
            RectTransform buttonB = CreateRect("Delete", row, new Vector2(250f, 50f));
            Vector2 originalA = buttonA.sizeDelta;
            Vector2 originalB = buttonB.sizeDelta;

            try
            {
                bool structural = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "IsStructuralLayout",
                    canvas,
                    row);

                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "RepairNestedStructuralLayouts",
                    canvas,
                    canvas,
                    0);

                Assert.That(structural, Is.False);
                Assert.That(changed, Is.False);
                AssertVector(buttonA.sizeDelta, originalA);
                AssertVector(buttonB.sizeDelta, originalB);
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        private static void AddMainMenuControls(RectTransform menuRoot)
        {
            for (int i = 0; i < 4; i++)
            {
                RectTransform button = CreateRect("Menu Button " + i, menuRoot, new Vector2(400f, 50f));
                button.gameObject.AddComponent<Image>();
                button.gameObject.AddComponent<Button>();
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

        private static void AssertVector(Vector2 actual, Vector2 expected, string message = null)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.01f), message);
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.01f), message);
        }
    }
}
