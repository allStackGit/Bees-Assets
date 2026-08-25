using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerCompositionLayoutGuardTests
    {
        private const string LayoutTypeName =
            "Assets.Scripts.UI_Components.SquadMakerCompositionLayoutGuard";
        private const string SquadMakerTypeName = "Assets.Scripts.Scenes.SquadMaker";

        [Test]
        public void TrackedSceneContainsTheCompositionStructureUsedByResponsiveOwnership()
        {
            string scene = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scenes",
                "Squad Maker.unity"));

            Assert.That(scene, Does.Contain("value: Squad Settings"));
            Assert.That(scene, Does.Contain("value: Squad Composition"));
            Assert.That(scene, Does.Contain("m_Name: Formations"));
            Assert.That(scene, Does.Contain("m_Name: Lower Buttons"));
            Assert.That(scene, Does.Contain("m_Name: Drop Zone"));
            Assert.That(scene, Does.Contain("DropZone:"));
            Assert.That(scene, Does.Contain("SquadMakerSupplyCapacityLabel:"));
            Assert.That(scene, Does.Contain("SquadNameInput:"));
            Assert.That(scene, Does.Contain("SquadShipCount:"));
            Assert.That(scene, Does.Contain("SquadColorPickerButton:"));
        }

        [Test]
        public void CompositionUsesRelationshipsInsteadOfLiveResolutionSpecificCoordinates()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform settingsLeft = CreateTopLeftRect(
                "Details",
                settings,
                new Vector2(240f, 120f),
                new Vector2(120f, -80f));
            RectTransform settingsCenter = CreateTopLeftRect(
                "Preview",
                settings,
                new Vector2(140f, 160f),
                new Vector2(390f, -100f));

            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform supply = CreateTopLeftRect(
                "Supply Capacity",
                composition,
                new Vector2(270f, 30f),
                new Vector2(135f, -15f));
            RectTransform squadName = CreateTopLeftRect(
                "Squad Name",
                composition,
                new Vector2(200f, 30f),
                new Vector2(370f, -15f));
            RectTransform color = CreateTopLeftRect(
                "Squad Color",
                composition,
                new Vector2(95f, 30f),
                new Vector2(515f, -15f));
            RectTransform count = CreateRect("Squad Count", composition, new Vector2(80f, 30f));
            count.anchorMin = Vector2.one;
            count.anchorMax = Vector2.one;
            count.anchoredPosition = new Vector2(-40f, -15f);

            RectTransform formations = CreateRect("Formations", composition, new Vector2(30f, 410f));
            formations.anchorMin = new Vector2(0f, 0.5f);
            formations.anchorMax = new Vector2(0f, 0.5f);
            formations.anchoredPosition = new Vector2(0f, -25f);

            RectTransform dropZone = CreateRect("Drop Zone", composition, new Vector2(600f, 340f));
            dropZone.anchoredPosition = new Vector2(7f, -5f);

            RectTransform actionRow = CreateRect("Lower Buttons", composition, new Vector2(100f, 100f));
            actionRow.anchoredPosition = new Vector2(-200f, -230f);
            CreateActionButton("Save", actionRow, 60f, 0f);
            CreateActionButton("Clear", actionRow, 120f, 80f);
            CreateActionButton("Duplicate", actionRow, 80f, 200f);
            CreateActionButton("Delete", actionRow, 70f, 300f);

            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "DropZone", dropZone.gameObject);

            float[] settingsLeftFractions = GetHorizontalFractions(settings, settingsLeft);
            float[] settingsCenterFractions = GetHorizontalFractions(settings, settingsCenter);
            float[] supplyFractions = GetHorizontalFractions(composition, supply);
            float[] nameFractions = GetHorizontalFractions(composition, squadName);
            float[] colorFractions = GetHorizontalFractions(composition, color);
            float[] countFractions = GetHorizontalFractions(composition, count);
            Vector4 dropMargins = GetMargins(composition, dropZone);

            System.Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(
                layoutType,
                "Capture",
                squadMaker,
                settings,
                composition);

            try
            {
                Assert.That(reference, Is.Not.Null);

                Vector2[] liveSizes =
                {
                    new Vector2(620f, 420f),
                    new Vector2(775f, 525f),
                    new Vector2(1240f, 420f),
                    new Vector2(930f, 840f),
                    new Vector2(1860f, 630f),
                    new Vector2(620f, 420f)
                };

                for (int index = 0; index < liveSizes.Length; index++)
                {
                    Vector2 liveSize = liveSizes[index];
                    settings.sizeDelta = new Vector2(liveSize.x, settings.rect.height);
                    composition.sizeDelta = liveSize;
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                    AssertHorizontalFractions(settings, settingsLeft, settingsLeftFractions);
                    AssertHorizontalFractions(settings, settingsCenter, settingsCenterFractions);
                    AssertHorizontalFractions(composition, supply, supplyFractions);
                    AssertHorizontalFractions(composition, squadName, nameFractions);
                    AssertHorizontalFractions(composition, color, colorFractions);
                    AssertHorizontalFractions(composition, count, countFractions);
                    AssertDropZoneMargins(composition, dropZone, dropMargins);
                    AssertFormationsFlushLeft(composition, formations);
                    AssertCompactBottomActionRow(composition, actionRow);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(manager);
                UnityEngine.Object.DestroyImmediate(settings.gameObject);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        private static RectTransform CreateActionButton(
            string name,
            RectTransform parent,
            float width,
            float x)
        {
            RectTransform button = CreateRect(name, parent, new Vector2(width, 30f));
            button.anchorMin = new Vector2(0f, 0.5f);
            button.anchorMax = new Vector2(0f, 0.5f);
            button.anchoredPosition = new Vector2(x, 0f);
            return button;
        }

        private static RectTransform CreateTopLeftRect(
            string name,
            RectTransform parent,
            Vector2 size,
            Vector2 anchoredPosition)
        {
            RectTransform rect = CreateRect(name, parent, size);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPosition;
            return rect;
        }

        private static float[] GetHorizontalFractions(RectTransform owner, RectTransform rect)
        {
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(owner, rect);
            return new[]
            {
                (bounds.min.x - owner.rect.xMin) / owner.rect.width,
                (bounds.max.x - owner.rect.xMin) / owner.rect.width
            };
        }

        private static void AssertHorizontalFractions(
            RectTransform owner,
            RectTransform rect,
            float[] expected)
        {
            float[] actual = GetHorizontalFractions(owner, rect);
            Assert.That(actual[0], Is.EqualTo(expected[0]).Within(0.002f), rect.name + " left fraction");
            Assert.That(actual[1], Is.EqualTo(expected[1]).Within(0.002f), rect.name + " right fraction");
        }

        private static Vector4 GetMargins(RectTransform owner, RectTransform rect)
        {
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(owner, rect);
            return new Vector4(
                bounds.min.x - owner.rect.xMin,
                owner.rect.xMax - bounds.max.x,
                owner.rect.yMax - bounds.max.y,
                bounds.min.y - owner.rect.yMin);
        }

        private static void AssertDropZoneMargins(
            RectTransform owner,
            RectTransform dropZone,
            Vector4 expectedMargins)
        {
            Vector4 actual = GetMargins(owner, dropZone);
            Assert.That(actual.x, Is.EqualTo(expectedMargins.x).Within(0.01f), "Drop Zone left margin");
            Assert.That(actual.y, Is.EqualTo(expectedMargins.y).Within(0.01f), "Drop Zone right margin");
            Assert.That(actual.z, Is.EqualTo(expectedMargins.z).Within(0.01f), "Drop Zone top margin");
            Assert.That(actual.w, Is.EqualTo(expectedMargins.w).Within(0.01f), "Drop Zone bottom margin");
        }

        private static void AssertFormationsFlushLeft(
            RectTransform composition,
            RectTransform formations)
        {
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(composition, formations);
            Assert.That(bounds.min.x, Is.EqualTo(composition.rect.xMin).Within(0.01f));
        }

        private static void AssertCompactBottomActionRow(
            RectTransform composition,
            RectTransform actionRow)
        {
            Assert.That(actionRow.anchorMin, Is.EqualTo(new Vector2(0f, 0f)));
            Assert.That(actionRow.anchorMax, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(actionRow.rect.width, Is.EqualTo(composition.rect.width).Within(0.01f));

            Bounds rowBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(composition, actionRow);
            Assert.That(rowBounds.min.y, Is.EqualTo(composition.rect.yMin).Within(0.01f));

            HorizontalLayoutGroup layout = actionRow.GetComponent<HorizontalLayoutGroup>();
            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.childAlignment, Is.EqualTo(TextAnchor.MiddleCenter));
            Assert.That(layout.childForceExpandWidth, Is.False);

            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            for (int index = 0; index < actionRow.childCount; index++)
            {
                RectTransform child = actionRow.GetChild(index) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                Bounds childBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(composition, child);
                minX = Mathf.Min(minX, childBounds.min.x);
                maxX = Mathf.Max(maxX, childBounds.max.x);
            }

            Assert.That((minX + maxX) * 0.5f, Is.EqualTo(composition.rect.center.x).Within(0.05f));
            Assert.That(maxX - minX, Is.LessThan(composition.rect.width));
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
