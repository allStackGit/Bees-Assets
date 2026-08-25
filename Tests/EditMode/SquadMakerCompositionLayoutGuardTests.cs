using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerCompositionLayoutGuardTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.SquadMakerCompositionLayoutGuard";

        [Test]
        public void TrackedSceneContainsTheCompositionControlsOwnedByTheResponsiveGuard()
        {
            string scene = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scenes",
                "Squad Maker.unity"));

            Assert.That(scene, Does.Contain("m_Name: Formations"));
            Assert.That(scene, Does.Contain("m_Name: Lower Buttons"));
            Assert.That(scene, Does.Contain("SquadShipCount:"));
            Assert.That(scene, Does.Contain("value: Squad Composition"));
        }

        [Test]
        public void CompositionControlsRemainAttachedToIntendedEdgesAcrossWideAndTallSizes()
        {
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform formations = CreateRect("Formations", composition, new Vector2(30f, 410f));
            formations.anchorMin = new Vector2(0f, 0.5f);
            formations.anchorMax = new Vector2(0f, 0.5f);
            formations.anchoredPosition = new Vector2(0f, -25f);

            RectTransform lowerButtons = CreateRect("Lower Buttons", composition, new Vector2(100f, 100f));
            lowerButtons.anchorMin = new Vector2(0.5f, 0.5f);
            lowerButtons.anchorMax = new Vector2(0.5f, 0.5f);
            lowerButtons.anchoredPosition = new Vector2(-200f, -230f);

            RectTransform squadShipCount = CreateRect("Squad Ship Count", composition, new Vector2(80f, 30f));
            squadShipCount.anchorMin = Vector2.one;
            squadShipCount.anchorMax = Vector2.one;
            squadShipCount.anchoredPosition = new Vector2(-40f, -15f);

            System.Type guardType = RuntimeAssembly.GetType(GuardTypeName);

            try
            {
                RuntimeAssembly.InvokeStatic(
                    guardType,
                    "ApplyReferenceEdgePins",
                    composition,
                    formations,
                    lowerButtons,
                    squadShipCount);

                AssertCompositionEdgeOwnership(composition, formations, lowerButtons, squadShipCount);

                composition.sizeDelta = new Vector2(1254f, 420f);
                AssertCompositionEdgeOwnership(composition, formations, lowerButtons, squadShipCount);

                composition.sizeDelta = new Vector2(620f, 651f);
                AssertCompositionEdgeOwnership(composition, formations, lowerButtons, squadShipCount);

                composition.sizeDelta = new Vector2(1254f, 651f);
                AssertCompositionEdgeOwnership(composition, formations, lowerButtons, squadShipCount);

                // Reapplying the repair after a display change must not accumulate any offset.
                RuntimeAssembly.InvokeStatic(
                    guardType,
                    "ApplyReferenceEdgePins",
                    composition,
                    formations,
                    lowerButtons,
                    squadShipCount);
                AssertCompositionEdgeOwnership(composition, formations, lowerButtons, squadShipCount);

                composition.sizeDelta = new Vector2(620f, 420f);
                AssertCompositionEdgeOwnership(composition, formations, lowerButtons, squadShipCount);
            }
            finally
            {
                Object.DestroyImmediate(composition.gameObject);
            }
        }

        private static void AssertCompositionEdgeOwnership(
            RectTransform composition,
            RectTransform formations,
            RectTransform lowerButtons,
            RectTransform squadShipCount)
        {
            Bounds formationBounds =
                RectTransformUtility.CalculateRelativeRectTransformBounds(composition, formations);
            Bounds lowerButtonsBounds =
                RectTransformUtility.CalculateRelativeRectTransformBounds(composition, lowerButtons);
            Bounds squadShipCountBounds =
                RectTransformUtility.CalculateRelativeRectTransformBounds(composition, squadShipCount);

            Assert.That(formations.anchorMin.x, Is.Zero);
            Assert.That(formations.anchorMax.x, Is.Zero);
            Assert.That(
                formationBounds.min.x,
                Is.EqualTo(composition.rect.xMin).Within(0.01f),
                "BLARP/Formations must be flush with the inside of the live composition's left edge.");

            Assert.That(lowerButtons.anchorMin.y, Is.Zero);
            Assert.That(lowerButtons.anchorMax.y, Is.Zero);
            Assert.That(
                lowerButtonsBounds.center.y - composition.rect.yMin,
                Is.EqualTo(-20f).Within(0.01f),
                "The save/clear/duplicate/delete group must retain its authored bottom-relative pivot.");

            Assert.That(squadShipCount.anchorMin.x, Is.Zero);
            Assert.That(squadShipCount.anchorMax.x, Is.Zero);
            Assert.That(
                squadShipCountBounds.center.x - composition.rect.xMin,
                Is.EqualTo(580f).Within(0.01f),
                "The 0/10 squad count must remain at its authored header position instead of following ultrawide surplus.");
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
