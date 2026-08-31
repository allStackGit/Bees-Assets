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
        public void TrackedNestedFooterGeometryKeepsFullFooterAndAlignsStartTestToBodyBoundary()
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

            RectTransform start = CreateRect("START", actionRow, new Vector2(85f, 30f));
            RectTransform test = CreateRect("TEST", actionRow, new Vector2(85f, 30f));

            // An unrelated footer control represents BACK/other footer content. The START/TEST
            // repair must not move it or shrink the Footer around it.
            RectTransform back = CreateRect("BACK", footer, new Vector2(220f, 40f));
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
                float buttonTopBefore = Mathf.Max(startBefore.max.y, testBefore.max.y);
                Assert.That(footer.rect.yMax - buttonTopBefore, Is.EqualTo(12.5f).Within(0.01f),
                    "The fixture mirrors Footer/Right Side/Start Buttons from Squad Maker.unity and must reproduce the reported strip before repair.");

                Vector2 offset = (Vector2)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateFooterActionRowOffset",
                    footer,
                    actionRow,
                    start,
                    test);

                Assert.That(offset.x, Is.EqualTo(0f).Within(0.01f));
                Assert.That(offset.y, Is.EqualTo(12.5f).Within(0.01f));
                actionRow.anchoredPosition += offset;

                Bounds startAfter = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, start);
                Bounds testAfter = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, test);
                Bounds backAfter = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, back);
                float buttonTopAfter = Mathf.Max(startAfter.max.y, testAfter.max.y);
                float buttonBottomAfter = Mathf.Min(startAfter.min.y, testAfter.min.y);

                Assert.That(buttonTopAfter, Is.EqualTo(footer.rect.yMax).Within(0.01f),
                    "START/TEST must meet the body/footer boundary without the 12.5-unit strip.");
                Assert.That(buttonBottomAfter, Is.GreaterThanOrEqualTo(footer.rect.yMin - 0.01f),
                    "Moving the action row to the body boundary must keep the buttons inside the Footer.");
                Assert.That(footer.rect.height, Is.EqualTo(51f).Within(0.01f),
                    "The complete authored Footer must remain 51 units high so bottom controls are not clipped.");
                Assert.That(backAfter.min, Is.EqualTo(backBefore.min));
                Assert.That(backAfter.max, Is.EqualTo(backBefore.max),
                    "Aligning START/TEST must not translate unrelated Footer controls such as BACK.");

                Vector2 secondOffset = (Vector2)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateFooterActionRowOffset",
                    footer,
                    actionRow,
                    start,
                    test);
                Assert.That(secondOffset.sqrMagnitude, Is.LessThan(0.0001f),
                    "The invariant solver must be idempotent rather than accumulating vertical drift.");
            }
            finally
            {
                Object.DestroyImmediate(footer.gameObject);
            }
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
