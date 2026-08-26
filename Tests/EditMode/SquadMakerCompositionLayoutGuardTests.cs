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
        private const string LayoutTypeName = "Assets.Scripts.UI_Components.SquadMakerCompositionLayoutGuard";
        private const string SquadMakerTypeName = "Assets.Scripts.Scenes.SquadMaker";

        [Test]
        public void TrackedSceneContainsTheCompositionStructureUsedByResponsiveOwnership()
        {
            string scene = File.ReadAllText(Path.Combine(Application.dataPath, "Scenes", "Squad Maker.unity"));
            Assert.That(scene, Does.Contain("value: Squad Settings"));
            Assert.That(scene, Does.Contain("value: Squad Composition"));
            Assert.That(scene, Does.Contain("value: Ship Selector Column"));
            Assert.That(scene, Does.Contain("value: Saved Squads Column"));
            Assert.That(scene, Does.Contain("value: Chosen Squads Column"));
            Assert.That(scene, Does.Contain("m_Name: Formations"));
            Assert.That(scene, Does.Contain("m_Name: Lower Buttons"));
            Assert.That(scene, Does.Contain("m_Name: Drop Zone"));
            Assert.That(scene, Does.Contain("DropZone:"));
            Assert.That(scene, Does.Contain("ColorPicker:"));
            Assert.That(scene, Does.Contain("SquadMakerSupplyCapacityLabel:"));
            Assert.That(scene, Does.Contain("SquadNameInput:"));
            Assert.That(scene, Does.Contain("SquadShipCount:"));
            Assert.That(scene, Does.Contain("SquadColorPickerButton:"));
        }

        [Test]
        public void ManualSettingsAndActionRowRemainResponsiveWhileBlarpOwnsSeparateRail()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform details = CreateTopLeftRect("Details", settings, new Vector2(240f, 120f), new Vector2(120f, -80f));
            RectTransform preview = CreateTopLeftRect("Preview", settings, new Vector2(140f, 160f), new Vector2(390f, -100f));
            float[] detailsFractions = GetHorizontalFractions(settings, details);
            float[] previewFractions = GetHorizontalFractions(settings, preview);

            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform formations = CreateRect("Formations", composition, new Vector2(30f, 410f));
            formations.anchorMin = new Vector2(0f, 0.5f);
            formations.anchorMax = new Vector2(0f, 0.5f);
            RectTransform dropZone = CreateRect("Drop Zone", composition, new Vector2(600f, 340f));
            dropZone.anchoredPosition = new Vector2(7f, -5f);
            RectTransform actionRow = CreateRect("Lower Buttons", composition, new Vector2(100f, 100f));
            CreateActionButton("Save", actionRow, 60f, 0f);
            CreateActionButton("Clear", actionRow, 120f, 80f);
            CreateActionButton("Duplicate", actionRow, 80f, 200f);
            CreateActionButton("Delete", actionRow, 70f, 300f);

            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "DropZone", dropZone.gameObject);
            System.Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(layoutType, "Capture", squadMaker, settings, composition);

            try
            {
                Vector2[] sizes =
                {
                    new Vector2(620f, 420f),
                    new Vector2(775f, 525f),
                    new Vector2(1240f, 420f),
                    new Vector2(930f, 840f),
                    new Vector2(620f, 420f)
                };
                for (int i = 0; i < sizes.Length; i++)
                {
                    settings.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, sizes[i].x);
                    composition.sizeDelta = sizes[i];
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);
                    AssertHorizontalFractions(settings, details, detailsFractions);
                    AssertHorizontalFractions(settings, preview, previewFractions);
                    Bounds rail = BoundsIn(composition, formations);
                    Bounds work = BoundsIn(composition, dropZone);
                    Assert.That(rail.min.x, Is.GreaterThanOrEqualTo(composition.rect.xMin - 0.02f));
                    Assert.That(rail.max.x, Is.LessThanOrEqualTo(work.min.x - 3.9f));
                    Assert.That(actionRow.rect.yMin, Is.EqualTo(composition.rect.yMin).Within(0.02f));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(manager);
                UnityEngine.Object.DestroyImmediate(settings.gameObject);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        [Test]
        public void EnabledSettingsLayoutRemainsNativeOwnerAcrossArbitraryWidths()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            HorizontalLayoutGroup layout = settings.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlWidth = false;
            layout.childForceExpandWidth = true;
            RectTransform left = CreateRect("Left", settings, new Vector2(200f, 298f));
            RectTransform right = CreateRect("Right", settings, new Vector2(420f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            System.Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(layoutType, "Capture", null, settings, composition);

            try
            {
                float[] widths = { 620f, 930f, 1240f, 775f, 620f };
                for (int i = 0; i < widths.Length; i++)
                {
                    settings.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, widths[i]);
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);
                    Assert.That(layout.childControlWidth, Is.True);
                    Assert.That(layout.childForceExpandWidth, Is.False);
                    Assert.That(left.rect.width, Is.EqualTo(widths[i] * 200f / 620f).Within(0.02f));
                    Assert.That(right.rect.width, Is.EqualTo(widths[i] * 420f / 620f).Within(0.02f));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings.gameObject);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        private static RectTransform CreateActionButton(string name, RectTransform parent, float width, float x)
        {
            RectTransform button = CreateRect(name, parent, new Vector2(width, 30f));
            button.anchoredPosition = new Vector2(x, 0f);
            return button;
        }

        private static RectTransform CreateTopLeftRect(string name, RectTransform parent, Vector2 size, Vector2 anchoredPosition)
        {
            RectTransform rect = CreateRect(name, parent, size);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            return rect;
        }

        private static RectTransform CreateRect(string name, RectTransform parent, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            if (parent != null) rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            return rect;
        }

        private static float[] GetHorizontalFractions(RectTransform owner, RectTransform child)
        {
            Bounds bounds = BoundsIn(owner, child);
            return new[]
            {
                (bounds.min.x - owner.rect.xMin) / owner.rect.width,
                (bounds.max.x - owner.rect.xMin) / owner.rect.width
            };
        }

        private static void AssertHorizontalFractions(RectTransform owner, RectTransform child, float[] expected)
        {
            float[] actual = GetHorizontalFractions(owner, child);
            Assert.That(actual[0], Is.EqualTo(expected[0]).Within(0.002f));
            Assert.That(actual[1], Is.EqualTo(expected[1]).Within(0.002f));
        }

        private static Bounds BoundsIn(RectTransform owner, RectTransform child)
        {
            return RectTransformUtility.CalculateRelativeRectTransformBounds(owner, child);
        }
    }
}
