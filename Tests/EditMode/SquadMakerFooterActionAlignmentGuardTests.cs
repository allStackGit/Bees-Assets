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
        public void TrackedFooterGeometryMovesOnlyStartTestRowToBodyBoundary()
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

            try
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(actionRow);

                Bounds startBefore = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, start);
                Bounds testBefore = RectTransformUtility.CalculateRelativeRectTransformBounds(footer, test);
                float buttonTopBefore = Mathf.Max(startBefore.max.y, testBefore.max.y);
                Assert.That(footer.rect.yMax - buttonTopBefore, Is.EqualTo(12.5f).Within(0.01f),
                    "This fixture mirrors Footer/Right Side/Start Buttons from Squad Maker.unity and must reproduce the visible authored strip before the repair.");

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
                float buttonTopAfter = Mathf.Max(startAfter.max.y, testAfter.max.y);

                Assert.That(buttonTopAfter, Is.EqualTo(footer.rect.yMax).Within(0.01f),
                    "START/TEST must meet the body/footer boundary without a gray strip.");
                Assert.That(footer.rect.height, Is.EqualTo(51f).Within(0.01f),
                    "The authored footer itself must remain 51 units high; shrinking it clips unrelated footer controls.");
            }
            finally
            {
                Object.DestroyImmediate(footer.gameObject);
            }
        }

        [Test]
        public void RecomputingFromAuthoredPositionDoesNotAccumulateVerticalDrift()
        {
            RectTransform footer = CreateRect("Footer", null, new Vector2(1366f, 51f));
            RectTransform rightSide = CreateRect("Right Side", footer, new Vector2(220f, 50f));
            rightSide.anchorMin = Vector2.one;
            rightSide.anchorMax = Vector2.one;
            rightSide.anchoredPosition = new Vector2(-130f, -37.5f);

            RectTransform actionRow = CreateRect("Start Buttons", rightSide, new Vector2(210f, 70f));
            actionRow.anchoredPosition = new Vector2(25f, 0f);
            RectTransform start = CreateRect("START", actionRow, new Vector2(85f, 30f));
            start.anchoredPosition = new Vector2(0f, 10f);

            Vector2 authored = actionRow.anchoredPosition;
            try
            {
                for (int pass = 0; pass < 4; pass++)
                {
                    actionRow.anchoredPosition = authored;
                    Vector2 offset = (Vector2)RuntimeAssembly.InvokeStatic(
                        RuntimeAssembly.GetType(GuardTypeName),
                        "CalculateFooterActionRowOffset",
                        footer,
                        actionRow,
                        start,
                        null);
                    actionRow.anchoredPosition = authored + offset;
                    Assert.That(actionRow.anchoredPosition, Is.EqualTo(authored + offset));
                }
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
