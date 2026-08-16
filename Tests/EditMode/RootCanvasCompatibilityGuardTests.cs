using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RootCanvasCompatibilityGuardTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.RootCanvasCompatibilityGuard";

        [Test]
        public void LegacyScreenLayoutOwnerCanBeStretchedToActualParent()
        {
            RectTransform parent = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform owner = CreateRect("Screen Layout", parent, new Vector2(1366f, 768f));
            owner.gameObject.AddComponent<VerticalLayoutGroup>();

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "StretchToParent",
                    owner);

                Assert.That(changed, Is.True);
                AssertVector(owner.anchorMin, Vector2.zero);
                AssertVector(owner.anchorMax, Vector2.one);
                AssertVector(owner.offsetMin, Vector2.zero);
                AssertVector(owner.offsetMax, Vector2.zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void InsetFullStretchPanelIsNotClassifiedAsViewport()
        {
            RectTransform parent = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform panel = CreateRect("MainPanel", parent, new Vector2(1000f, 800f));
            panel.anchorMin = Vector2.zero;
            panel.anchorMax = Vector2.one;
            panel.offsetMin = new Vector2(300f, 50f);
            panel.offsetMax = new Vector2(-300f, -50f);
            panel.gameObject.AddComponent<VerticalLayoutGroup>();

            try
            {
                bool representsViewport = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "RectRepresentsViewport",
                    panel,
                    parent,
                    new Vector2(1366f, 768f));

                Assert.That(representsViewport, Is.False,
                    "A deliberately inset full-stretch menu panel must retain its authored border spacing instead of being expanded to its parent.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void FullStretchOwnerOnSmallerCanvasStillRepresentsViewport()
        {
            RectTransform parent = CreateRect("Canvas", null, new Vector2(1024f, 768f));
            RectTransform owner = CreateRect("Screen Layout", parent, new Vector2(1024f, 768f));
            owner.anchorMin = Vector2.zero;
            owner.anchorMax = Vector2.one;
            owner.offsetMin = Vector2.zero;
            owner.offsetMax = Vector2.zero;

            try
            {
                bool representsViewport = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "RectRepresentsViewport",
                    owner,
                    parent,
                    new Vector2(1366f, 768f));

                Assert.That(representsViewport, Is.True,
                    "A true full-parent viewport must remain eligible for recursive responsive repair even when the live canvas is narrower than the reference resolution.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void LayoutGroupOwnedChildIsNotReanchoredByCompatibilityPass()
        {
            RectTransform parent = CreateRect("Layout Parent", null, new Vector2(1600f, 900f));
            parent.gameObject.AddComponent<VerticalLayoutGroup>();
            RectTransform child = CreateRect("Layout Child", parent, new Vector2(1366f, 718f));
            Vector2 originalAnchorMin = child.anchorMin;
            Vector2 originalAnchorMax = child.anchorMax;

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "StretchToParent",
                    child);

                Assert.That(changed, Is.False,
                    "The compatibility pass must repair the layout owner, not reanchor children driven by that LayoutGroup.");
                AssertVector(child.anchorMin, originalAnchorMin);
                AssertVector(child.anchorMax, originalAnchorMax);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void TallViewportGivesSurplusHeightToDominantContentInsteadOfWhiteBottomStrip()
        {
            RectTransform owner = CreateRect("Screen Layout", null, new Vector2(1600f, 900f));
            VerticalLayoutGroup layout = owner.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            RectTransform main = CreateRect("Main Container", owner, new Vector2(1600f, 718f));
            RectTransform footer = CreateRect("Footer", owner, new Vector2(1600f, 50f));

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "FitDominantVerticalLayoutChild",
                    owner);

                Assert.That(changed, Is.True);
                Assert.That(main.rect.height, Is.EqualTo(850f).Within(0.01f),
                    "The main body should absorb the taller viewport while the footer keeps its authored height.");
                Assert.That(footer.rect.height, Is.EqualTo(50f).Within(0.01f));
                Assert.That(main.rect.height + footer.rect.height, Is.EqualTo(owner.rect.height).Within(0.01f),
                    "No unused vertical band should remain for the white root backer to show through.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner.gameObject);
            }
        }

        [Test]
        public void ReferenceHeightPreservesAuthoredMainAndFooterSplit()
        {
            RectTransform owner = CreateRect("Screen Layout", null, new Vector2(1366f, 768f));
            VerticalLayoutGroup layout = owner.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            RectTransform main = CreateRect("Main Container", owner, new Vector2(1366f, 718f));
            RectTransform footer = CreateRect("Footer", owner, new Vector2(1366f, 50f));

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "FitDominantVerticalLayoutChild",
                    owner);

                Assert.That(changed, Is.False,
                    "The compatibility pass should not perturb the original 1366x768 layout when it already fills the viewport.");
                Assert.That(main.rect.height, Is.EqualTo(718f).Within(0.01f));
                Assert.That(footer.rect.height, Is.EqualTo(50f).Within(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner.gameObject);
            }
        }

        [Test]
        public void DirectInteractiveIslandIsMovedFullyInsideCanvasWithoutChangingItsSize()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform button = CreateRect("Continue Button", canvas, new Vector2(150f, 50f));
            button.anchoredPosition = new Vector2(-760f, -440f);
            Vector2 originalSize = button.sizeDelta;

            try
            {
                Bounds before = RectTransformUtility.CalculateRelativeRectTransformBounds(canvas, button);
                Assert.That(before.min.x, Is.LessThan(canvas.rect.xMin));
                Assert.That(before.min.y, Is.LessThan(canvas.rect.yMin));

                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ClampIslandToCanvas",
                    button,
                    canvas);

                Bounds after = RectTransformUtility.CalculateRelativeRectTransformBounds(canvas, button);
                Assert.That(changed, Is.True);
                Assert.That(after.min.x, Is.EqualTo(canvas.rect.xMin).Within(0.01f));
                Assert.That(after.min.y, Is.EqualTo(canvas.rect.yMin).Within(0.01f));
                AssertVector(button.sizeDelta, originalSize);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void ScreenNavigationButtonGetsVisibleMarginEvenWhenAuthoredBarelyInside()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform button = CreateRect("Back Button", canvas, new Vector2(150f, 50f));
            button.anchorMin = Vector2.zero;
            button.anchorMax = Vector2.zero;
            button.anchoredPosition = new Vector2(85f, 35f);

            try
            {
                Bounds before = RectTransformUtility.CalculateRelativeRectTransformBounds(canvas, button);
                Assert.That(before.min.x, Is.EqualTo(canvas.rect.xMin + 10f).Within(0.01f));
                Assert.That(before.min.y, Is.EqualTo(canvas.rect.yMin + 10f).Within(0.01f));

                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ClampIslandToCanvasWithMargin",
                    button,
                    canvas,
                    15f);

                Bounds after = RectTransformUtility.CalculateRelativeRectTransformBounds(canvas, button);
                Assert.That(changed, Is.True);
                Assert.That(after.min.x, Is.EqualTo(canvas.rect.xMin + 15f).Within(0.01f));
                Assert.That(after.min.y, Is.EqualTo(canvas.rect.yMin + 15f).Within(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void InBoundsInteractiveIslandIsNotMoved()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform button = CreateRect("Other Button", canvas, new Vector2(150f, 50f));
            button.anchoredPosition = new Vector2(-700f, -400f);
            Vector3 originalPosition = button.position;

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ClampIslandToCanvas",
                    button,
                    canvas);

                Assert.That(changed, Is.False);
                Assert.That(Vector3.Distance(button.position, originalPosition), Is.LessThan(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void RuntimeGuardLeavesGameplaySquadTabPlacementToHudOwner()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "RootCanvasCompatibilityGuard.cs"));

            StringAssert.Contains("SceneManager.sceneLoaded", source);
            StringAssert.Contains("parent.GetComponent<LayoutGroup>() == null", source);
            StringAssert.Contains("child.GetComponentInChildren<Selectable>(true)", source);
            StringAssert.Contains("FitDominantVerticalLayoutChild(child);", source);
            StringAssert.Contains("RectRepresentsViewport(child, parent, referenceResolution)", source);
            StringAssert.Contains("ParentViewportCoverageThreshold = 0.99f", source);
            StringAssert.Contains("NavigationControlMargin = 15f", source);
            StringAssert.Contains("RequiresNavigationMargin(child)", source);
            StringAssert.DoesNotContain("FindNamedRectTransform(_canvasRect, \"Squad Tabs\"", source,
                "The compatibility pass must not fight GameHudLayoutGuard over scoreboard-relative squad-tab placement.");
            StringAssert.DoesNotContain("PinLayoutRootToCanvasCorner(_squadTabsRoot", source);
            StringAssert.DoesNotContain("Screen.safeArea", source,
                "Desktop compatibility repair should use the actual root canvas rather than inventing a safe-area inset.");
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

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.01f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.01f));
        }
    }
}
