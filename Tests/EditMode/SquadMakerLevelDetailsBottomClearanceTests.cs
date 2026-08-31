using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerLevelDetailsBottomClearanceTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerLevelDetailsFitGuard";

        [Test]
        public void LevelDetailsKeepSupplyCapacityFlushAboveFooterButtonsWithRealPanelExpansion()
        {
            RectTransform column = CreateRect(
                "Chosen Squads Column",
                null,
                new Vector2(222f, 718f));
            VerticalLayoutGroup layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.spacing = 0f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            RectTransform chosenList = CreateRect(
                "Chosen Squad List",
                column,
                new Vector2(222f, 278f));
            CreateRect("Level Title", column, new Vector2(222f, 25f));
            RectTransform details = CreateRect(
                "Level Details Container",
                column,
                new Vector2(222f, 350f));
            RectTransform supply = CreateRect(
                "Supply Capacity",
                column,
                new Vector2(222f, 35f));

            RectTransform start = CreateFooterButton("START", column, -55f);
            RectTransform test = CreateFooterButton("TEST", column, 55f);
            Component guard = column.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                RuntimeAssembly.SetField(guard, "_chosenColumn", column);
                RuntimeAssembly.SetField(guard, "_chosenListRow", chosenList);
                RuntimeAssembly.SetField(guard, "_detailsRow", details);
                RuntimeAssembly.SetField(guard, "_supplyRow", supply);
                RuntimeAssembly.SetField(guard, "_startButtonRect", start);
                RuntimeAssembly.SetField(guard, "_testButtonRect", test);
                RuntimeAssembly.SetField(guard, "_supplyText", null);
                RuntimeAssembly.SetField(guard, "_referenceChosenColumnHeight", 718f);
                RuntimeAssembly.SetField(guard, "_referenceDetailsHeight", 350f);
                RuntimeAssembly.SetField(guard, "_referenceSupplyHeight", 35f);
                RuntimeAssembly.SetField(guard, "_referenceGeometryCaptured", true);

                LayoutRebuilder.ForceRebuildLayoutImmediate(column);
                RuntimeAssembly.Invoke(guard, "ApplyFit");

                Assert.That(layout.childForceExpandHeight, Is.False,
                    "The generic Panel.prefab expansion must be disabled while the level report owns the flexible height.");
                Assert.That(details.rect.height, Is.LessThan(350f),
                    "The flexible report must yield when the footer occupies the bottom of the chosen-squads column.");

                Bounds supplyBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(column, supply);
                Bounds startBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(column, start);
                Bounds testBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(column, test);
                float footerTop = Mathf.Max(startBounds.max.y, testBounds.max.y);

                Assert.That(supplyBounds.min.y, Is.EqualTo(footerTop).Within(0.01f),
                    "The Supply Capacity row must clear START/TEST without reserving a visible strip of unused panel background.");

                details.gameObject.SetActive(false);
                RuntimeAssembly.Invoke(guard, "ApplyFit");
                Assert.That(layout.childForceExpandHeight, Is.True,
                    "Leaving level-details mode must restore the panel's authored expansion behavior.");
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
        }

        [Test]
        public void BottomClearanceUsesActualRenderedBoundsRatherThanNominalRowBudget()
        {
            RectTransform column = CreateRect(
                "Chosen Squads Column",
                null,
                new Vector2(222f, 718f));
            VerticalLayoutGroup layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);

            RectTransform supply = CreateRect(
                "Supply Capacity",
                column,
                new Vector2(222f, 35f));
            supply.anchorMin = new Vector2(0.5f, 0f);
            supply.anchorMax = new Vector2(0.5f, 0f);
            supply.pivot = new Vector2(0.5f, 0f);
            supply.anchoredPosition = new Vector2(0f, 10f);

            RectTransform start = CreateFooterButton("START", column, 0f);

            try
            {
                float clearance = (float)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateRequiredBottomClearance",
                    column,
                    supply,
                    start,
                    null);

                Assert.That(clearance, Is.EqualTo(30f).Within(0.01f),
                    "A 40px footer must move a row whose bottom is only 10px above the column edge by exactly the 30px overlap, without adding visible slack.");
            }
            finally
            {
                Object.DestroyImmediate(column.gameObject);
            }
        }

        private static RectTransform CreateFooterButton(
            string name,
            RectTransform parent,
            float x)
        {
            RectTransform button = CreateRect(name, parent, new Vector2(100f, 40f));
            LayoutElement layout = button.gameObject.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            button.anchorMin = new Vector2(0.5f, 0f);
            button.anchorMax = new Vector2(0.5f, 0f);
            button.pivot = new Vector2(0.5f, 0f);
            button.anchoredPosition = new Vector2(x, 0f);
            return button;
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
