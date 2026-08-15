using System;
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
                    "The compatibility pass must repair the layout owner, not rewrite children driven by that LayoutGroup.");
                AssertVector(child.anchorMin, originalAnchorMin);
                AssertVector(child.anchorMax, originalAnchorMax);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
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
        public void InBoundsInteractiveIslandIsNotMoved()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform button = CreateRect("Back Button", canvas, new Vector2(150f, 50f));
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
        public void SquadTabsRootCanBePinnedToActualCanvasTopLeft()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform tabs = CreateRect("Squad Tabs", canvas, new Vector2(300f, 40f));
            tabs.anchoredPosition = new Vector2(-500f, 250f);

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "PinLayoutRootToCanvasCorner",
                    tabs,
                    canvas,
                    false,
                    true);

                Bounds after = RectTransformUtility.CalculateRelativeRectTransformBounds(canvas, tabs);
                Assert.That(changed, Is.True);
                Assert.That(after.min.x, Is.EqualTo(canvas.rect.xMin).Within(0.01f));
                Assert.That(after.max.y, Is.EqualTo(canvas.rect.yMax).Within(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void RuntimeGuardStaysAtOwnershipBoundaries()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "RootCanvasCompatibilityGuard.cs"));

            StringAssert.Contains("SceneManager.sceneLoaded", source);
            StringAssert.Contains("parent.GetComponent<LayoutGroup>() == null", source);
            StringAssert.Contains("child.GetComponentInChildren<Selectable>(true)", source);
            StringAssert.Contains("ClampDirectInteractiveIslands(_canvasRect", source);
            StringAssert.Contains("FindNamedRectTransform(_canvasRect, \"Squad Tabs\"", source);
            StringAssert.Contains("PinLayoutRootToCanvasCorner(_squadTabsRoot, _canvasRect, false, true)", source);
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
