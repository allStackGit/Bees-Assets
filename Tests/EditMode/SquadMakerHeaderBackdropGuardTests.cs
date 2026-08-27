using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerHeaderBackdropGuardTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.SquadMakerHeaderBackdropGuard";
        private const string SquadMakerTypeName = "Assets.Scripts.Scenes.SquadMaker";
        private const string BackdropName = "Responsive Squad Header Backdrop";

        [Test]
        public void DirectCompositionHeaderGetsFullWidthBackdropWithoutMovingControls()
        {
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform supply = CreateHeaderRect("Supply Capacity", composition, 10f, 200f);
            supply.gameObject.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 1f);
            RectTransform name = CreateHeaderRect("Squad Name", composition, 220f, 220f);
            RectTransform color = CreateHeaderRect("COLOR", composition, 450f, 70f);
            RectTransform count = CreateHeaderRect("0 / 10", composition, 530f, 80f);

            Vector2 supplyPosition = supply.anchoredPosition;
            Vector2 namePosition = name.anchoredPosition;
            Vector2 colorPosition = color.anchoredPosition;
            Vector2 countPosition = count.anchoredPosition;
            Vector2 nameSize = name.sizeDelta;

            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "SquadMakerSupplyCapacityLabel", supply.gameObject);
            RuntimeAssembly.SetField(squadMaker, "SquadNameInput", name.gameObject);
            RuntimeAssembly.SetField(squadMaker, "SquadColorPickerButton", color.gameObject);
            RuntimeAssembly.SetField(squadMaker, "SquadShipCount", count.gameObject);

            Type guardType = RuntimeAssembly.GetType(GuardTypeName);
            Component guard = RuntimeAssembly.InvokeStatic(guardType, "EnsureFor", squadMaker) as Component;

            try
            {
                Assert.That(guard, Is.Not.Null,
                    "The direct-under-composition scene shape must receive the dedicated backdrop relay.");
                AssertBackdrop(composition, expectedWidth: 620f, expectedHeight: 30f);
                AssertControlsUnchanged(supply, name, color, count,
                    supplyPosition, namePosition, colorPosition, countPosition, nameSize);

                composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 2400f);
                RuntimeAssembly.Invoke(guard, "ApplyBackdrop");
                AssertBackdrop(composition, expectedWidth: 2400f, expectedHeight: 30f);
                AssertControlsUnchanged(supply, name, color, count,
                    supplyPosition, namePosition, colorPosition, countPosition, nameSize);

                composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 775f);
                RuntimeAssembly.Invoke(guard, "ApplyBackdrop");
                AssertBackdrop(composition, expectedWidth: 775f, expectedHeight: 30f);
                AssertControlsUnchanged(supply, name, color, count,
                    supplyPosition, namePosition, colorPosition, countPosition, nameSize);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(manager);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        private static void AssertBackdrop(RectTransform composition, float expectedWidth, float expectedHeight)
        {
            RectTransform backdrop = composition.Find(BackdropName) as RectTransform;
            Assert.That(backdrop, Is.Not.Null);
            Assert.That(backdrop.rect.width, Is.EqualTo(expectedWidth).Within(0.02f));
            Assert.That(backdrop.rect.height, Is.EqualTo(expectedHeight).Within(0.02f));
            Assert.That(backdrop.GetComponent<Image>(), Is.Not.Null);
            Assert.That(backdrop.GetComponent<Image>().raycastTarget, Is.False);
            Assert.That(backdrop.GetComponent<LayoutElement>(), Is.Not.Null);
            Assert.That(backdrop.GetComponent<LayoutElement>().ignoreLayout, Is.True);
        }

        private static void AssertControlsUnchanged(
            RectTransform supply,
            RectTransform name,
            RectTransform color,
            RectTransform count,
            Vector2 supplyPosition,
            Vector2 namePosition,
            Vector2 colorPosition,
            Vector2 countPosition,
            Vector2 nameSize)
        {
            Assert.That(supply.anchoredPosition, Is.EqualTo(supplyPosition));
            Assert.That(name.anchoredPosition, Is.EqualTo(namePosition));
            Assert.That(color.anchoredPosition, Is.EqualTo(colorPosition));
            Assert.That(count.anchoredPosition, Is.EqualTo(countPosition));
            Assert.That(name.sizeDelta, Is.EqualTo(nameSize));
        }

        private static RectTransform CreateHeaderRect(string name, RectTransform parent, float left, float width)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(width, 30f));
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, left, width);
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 0f, 30f);
            return rect;
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
