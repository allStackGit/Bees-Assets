using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerFooterActionAlignmentGuardTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerFooterActionAlignmentGuard";

        [Test]
        public void FooterTrimUsesCompleteControlEnvelopeAndPreservesAuthoredButtonPositions()
        {
            RectTransform footer = CreateRect("Footer", null, new Vector2(1366f, 51f));
            RectTransform rightSide = CreateRect("Right Side", footer, new Vector2(220f, 50f));
            rightSide.anchorMin = Vector2.one;
            rightSide.anchorMax = Vector2.one;
            rightSide.pivot = new Vector2(0.5f, 0.5f);
            rightSide.anchoredPosition = new Vector2(-130f, -37.5f);

            RectTransform actionRow = CreateRect("Start Buttons", rightSide, new Vector2(210f, 70f));
            actionRow.anchorMin = new Vector2(0.5f, 0.5f);
            actionRow.anchorMax = new Vector2(0.5f, 0.5f);
            actionRow.pivot = new Vector2(0.5f, 0.5f);
            actionRow.anchoredPosition = new Vector2(25f, 0f);

            HorizontalLayoutGroup layout = actionRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 20);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = false;
            layout.childControlHeight = false;

            RectTransform start = CreateButton("START", actionRow, new Vector2(85f, 30f));
            RectTransform test = CreateButton("TEST", actionRow, new Vector2(85f, 30f));

            // BACK is the tallest bottom-relative footer control in the tracked scene. Measuring only
            // START/TEST would produce 38.5 and clip BACK; the complete real control envelope is 40.
            RectTransform back = CreateButton("BACK", footer, new Vector2(220f, 40f));
            back.anchorMin = Vector2.zero;
            back.anchorMax = Vector2.zero;
            back.pivot = Vector2.zero;
            back.anchoredPosition = Vector2.zero;

            try
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(actionRow);

                Bounds startBefore = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, start);
                Bounds testBefore = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, test);
                Bounds backBefore = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, back);
                float rightSideBottomOffset = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateBottomOffset",
                    footer,
                    rightSide);
                float backBottomOffset = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateBottomOffset",
                    footer,
                    back);

                float measuredHeight = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateFooterControlEnvelopeHeight",
                    footer);

                Assert.That(measuredHeight, Is.EqualTo(40f).Within(0.01f),
                    "Footer height must come from the complete authored button envelope. BACK is 40 units tall from the footer bottom, so no guessed START/TEST margin may make the footer shorter.");

                // Simulate the structural owner reclaiming the measured 11 unused units.
                footer.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, measuredHeight);

                float rightCorrection = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateBottomRelativeCorrection",
                    footer,
                    rightSide,
                    rightSideBottomOffset);
                float backCorrection = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateBottomRelativeCorrection",
                    footer,
                    back,
                    backBottomOffset);

                rightSide.anchoredPosition += new Vector2(0f, rightCorrection);
                back.anchoredPosition += new Vector2(0f, backCorrection);
                LayoutRebuilder.ForceRebuildLayoutImmediate(actionRow);

                Bounds startAfter = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, start);
                Bounds testAfter = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, test);
                Bounds backAfter = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, back);

                Assert.That(rightCorrection, Is.EqualTo(11f).Within(0.01f),
                    "The top-anchored Right Side must be translated by exactly the reclaimed footer height so START/TEST do not move down with the smaller footer.");
                Assert.That(backCorrection, Is.EqualTo(0f).Within(0.01f),
                    "The bottom-anchored BACK button is already bottom-relative and must not move.");
                Assert.That(
                    (float)RuntimeAssembly.InvokeStatic(
                        RuntimeAssembly.GetType(GuardTypeName),
                        "CalculateBottomOffset",
                        footer,
                        rightSide),
                    Is.EqualTo(rightSideBottomOffset).Within(0.01f));
                Assert.That(
                    (float)RuntimeAssembly.InvokeStatic(
                        RuntimeAssembly.GetType(GuardTypeName),
                        "CalculateBottomOffset",
                        footer,
                        back),
                    Is.EqualTo(backBottomOffset).Within(0.01f));

                // The footer-local coordinates move when its height changes, so compare every control
                // by its distance from the footer bottom: this is the screen-stable relationship the
                // runtime guard preserves while the MainPanel body gains the reclaimed 11 units.
                AssertBottomRelativeBoundsEqual(footer, startBefore, 51f, startAfter, measuredHeight);
                AssertBottomRelativeBoundsEqual(footer, testBefore, 51f, testAfter, measuredHeight);
                AssertBottomRelativeBoundsEqual(footer, backBefore, 51f, backAfter, measuredHeight);

                Assert.That(footer.rect.yMax - startAfter.max.y, Is.EqualTo(1.5f).Within(0.01f),
                    "With the complete 40-unit footer envelope, START/TEST retain their authored position and sit 1.5 units below the reclaimed body boundary instead of being pulled upward into Supply Capacity.");
                Assert.That(footer.rect.yMax - backAfter.max.y, Is.EqualTo(0f).Within(0.01f),
                    "BACK defines the measured footer top and remains completely visible.");
            }
            finally
            {
                Object.DestroyImmediate(footer.gameObject);
            }
        }

        private static void AssertBottomRelativeBoundsEqual(
            RectTransform footer,
            Bounds before,
            float beforeFooterHeight,
            Bounds after,
            float afterFooterHeight)
        {
            float beforeFooterBottom = -beforeFooterHeight * 0.5f;
            float afterFooterBottom = footer.rect.yMin;
            Assert.That(after.min.y - afterFooterBottom,
                Is.EqualTo(before.min.y - beforeFooterBottom).Within(0.01f));
            Assert.That(after.max.y - afterFooterBottom,
                Is.EqualTo(before.max.y - beforeFooterBottom).Within(0.01f));
        }

        private static RectTransform CreateButton(
            string name,
            RectTransform parent,
            Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent, size);
            rect.gameObject.AddComponent<Image>();
            rect.gameObject.AddComponent<Button>();
            return rect;
        }

        private static RectTransform CreateRect(
            string name,
            RectTransform parent,
            Vector2 size)
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
