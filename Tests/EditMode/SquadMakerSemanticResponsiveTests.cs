using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerSemanticResponsiveTests
    {
        private const string LayoutTypeName =
            "Assets.Scripts.UI_Components.SquadMakerCompositionLayoutGuard";
        private const string SquadMakerTypeName = "Assets.Scripts.Scenes.SquadMaker";

        [Test]
        public void HeaderGivesHorizontalSurplusToSquadNameInsteadOfSeparatingCompactControls()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform supply = CreateTopRowRect("Supply Capacity", composition, 10f, 200f);
            RectTransform name = CreateTopRowRect("Squad Name", composition, 220f, 230f);
            RectTransform color = CreateTopRowRect("COLOR", composition, 460f, 70f);
            RectTransform count = CreateTopRowRect("0 / 10", composition, 540f, 70f);

            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "SquadMakerSupplyCapacityLabel", supply.gameObject);
            RuntimeAssembly.SetField(squadMaker, "SquadNameInput", name.gameObject);
            RuntimeAssembly.SetField(squadMaker, "SquadColorPickerButton", color.gameObject);
            RuntimeAssembly.SetField(squadMaker, "SquadShipCount", count.gameObject);

            System.Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(
                layoutType,
                "Capture",
                squadMaker,
                settings,
                composition);

            try
            {
                float referenceSupplyWidth = supply.rect.width;
                float referenceNameWidth = name.rect.width;
                float referenceColorWidth = color.rect.width;
                float referenceCountWidth = count.rect.width;

                float[] widths = { 620f, 930f, 1240f, 500f, 620f };
                for (int index = 0; index < widths.Length; index++)
                {
                    float width = widths[index];
                    composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                    Bounds supplyBounds = BoundsIn(composition, supply);
                    Bounds nameBounds = BoundsIn(composition, name);
                    Bounds colorBounds = BoundsIn(composition, color);
                    Bounds countBounds = BoundsIn(composition, count);

                    Assert.That(supplyBounds.max.x, Is.LessThanOrEqualTo(nameBounds.min.x + 0.02f));
                    Assert.That(nameBounds.max.x, Is.LessThanOrEqualTo(colorBounds.min.x + 0.02f));
                    Assert.That(colorBounds.max.x, Is.LessThanOrEqualTo(countBounds.min.x + 0.02f));
                    Assert.That(supplyBounds.min.x, Is.GreaterThanOrEqualTo(composition.rect.xMin - 0.02f));
                    Assert.That(countBounds.max.x, Is.LessThanOrEqualTo(composition.rect.xMax + 0.02f));

                    if (width >= 620f)
                    {
                        Assert.That(supply.rect.width, Is.EqualTo(referenceSupplyWidth).Within(0.02f));
                        Assert.That(color.rect.width, Is.EqualTo(referenceColorWidth).Within(0.02f));
                        Assert.That(count.rect.width, Is.EqualTo(referenceCountWidth).Within(0.02f));
                        Assert.That(
                            name.rect.width,
                            Is.EqualTo(referenceNameWidth + (width - 620f)).Within(0.02f),
                            "Only the editable squad-name field should absorb wide-layout surplus.");
                    }
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
        public void SquadRowsAlignNameImmediatelyAfterIconWithinLiveRowWidth()
        {
            RectTransform mainContainer = CreateRect("Main Container", null, new Vector2(1366f, 718f));
            RectTransform squadsColumn = CreateRect("Squads Column", mainContainer, new Vector2(484f, 718f));
            RectTransform savedColumn = CreateRect("Saved Squads Column", squadsColumn, new Vector2(262f, 718f));
            RectTransform chosenColumn = CreateRect("Chosen Squads Column", squadsColumn, new Vector2(222f, 718f));
            RectTransform savedList = CreateRect("Saved List", savedColumn, new Vector2(262f, 300f));
            RectTransform chosenList = CreateRect("Chosen List", chosenColumn, new Vector2(222f, 300f));
            RectTransform savedRow = CreateSquadRow("Saved Row", savedList, 262f, out RectTransform savedIcon, out TMP_Text savedLabel);
            RectTransform chosenRow = CreateSquadRow("Chosen Row", chosenList, 222f, out RectTransform chosenIcon, out TMP_Text chosenLabel);

            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "SavedSquadList", savedList.gameObject);
            RuntimeAssembly.SetField(squadMaker, "ChosenSquadList", chosenList.gameObject);

            System.Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(
                layoutType,
                "Capture",
                squadMaker,
                settings,
                composition);

            try
            {
                RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);
                AssertLeftAlignedSquadRow(savedRow, savedIcon, savedLabel);
                AssertLeftAlignedSquadRow(chosenRow, chosenIcon, chosenLabel);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(manager);
                UnityEngine.Object.DestroyImmediate(mainContainer.gameObject);
                UnityEngine.Object.DestroyImmediate(settings.gameObject);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        [Test]
        public void SettingsStructuralBackerAndPresetHeadingFollowTheLiveCenterWidth()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            VerticalLayoutGroup settingsLayout = settings.gameObject.AddComponent<VerticalLayoutGroup>();
            settingsLayout.childControlWidth = false;
            settingsLayout.childForceExpandWidth = false;
            settingsLayout.childControlHeight = false;
            settingsLayout.childForceExpandHeight = false;

            RectTransform info = CreateRect("Ship Info", settings, new Vector2(620f, 268f));
            RectTransform backer = CreateRect("Preset Backer", info, new Vector2(400f, 268f));
            backer.gameObject.AddComponent<Image>();
            RectTransform headingRect = CreateRect("Preset Heading", backer, new Vector2(200f, 30f));
            TextMeshProUGUI heading = headingRect.gameObject.AddComponent<TextMeshProUGUI>();
            heading.text = "Squad Presets";

            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            System.Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(
                layoutType,
                "Capture",
                null,
                settings,
                composition);

            try
            {
                settings.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 1240f);
                RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                Assert.That(info.rect.width, Is.EqualTo(settings.rect.width).Within(0.02f));
                Assert.That(
                    backer.rect.width,
                    Is.EqualTo(info.rect.width).Within(0.02f),
                    "The visible settings/presets backer should fill the live center region rather than retain a reference-width island.");
                Bounds headingBounds = BoundsIn(settings, headingRect);
                Assert.That(headingBounds.center.x, Is.EqualTo(settings.rect.center.x).Within(0.02f));
                Assert.That(heading.horizontalAlignment, Is.EqualTo(HorizontalAlignmentOptions.Center));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings.gameObject);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        [Test]
        public void FullBlarpHierarchyRemainsInsideTheActualDropWorkspace()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform formations = CreateRect("Formations", composition, new Vector2(30f, 410f));
            formations.anchorMin = new Vector2(0f, 0.5f);
            formations.anchorMax = new Vector2(0f, 0.5f);
            RectTransform protruding = CreateRect("BLARP Visual", formations, new Vector2(48f, 30f));
            protruding.anchoredPosition = new Vector2(-12f, 0f);

            RectTransform dropZone = CreateRect("Drop Zone", composition, new Vector2(580f, 340f));
            dropZone.anchoredPosition = new Vector2(15f, -5f);

            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "DropZone", dropZone.gameObject);
            System.Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(
                layoutType,
                "Capture",
                squadMaker,
                settings,
                composition);

            try
            {
                float[] widths = { 620f, 500f, 930f, 620f };
                for (int index = 0; index < widths.Length; index++)
                {
                    composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, widths[index]);
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                    Bounds work = BoundsIn(composition, dropZone);
                    Bounds blarp = RectTransformUtility.CalculateRelativeRectTransformBounds(composition, formations);
                    Assert.That(blarp.min.x, Is.GreaterThanOrEqualTo(work.min.x - 0.02f));
                    Assert.That(blarp.max.x, Is.LessThanOrEqualTo(work.max.x + 0.02f));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(manager);
                UnityEngine.Object.DestroyImmediate(settings.gameObject);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        private static RectTransform CreateTopRowRect(
            string name,
            RectTransform parent,
            float left,
            float width)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(width, 30f));
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, left, width);
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 0f, 30f);
            return rect;
        }

        private static RectTransform CreateSquadRow(
            string name,
            RectTransform parent,
            float width,
            out RectTransform icon,
            out TMP_Text label)
        {
            RectTransform row = CreateRect(name, parent, new Vector2(width, 30f));
            row.gameObject.AddComponent<Image>();

            icon = CreateRect("Icon", row, new Vector2(22f, 22f));
            icon.gameObject.AddComponent<Image>();
            icon.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 4f, 22f);

            GameObject labelObject = new GameObject("Squad Number", typeof(RectTransform), typeof(CanvasRenderer));
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.SetParent(row, false);
            labelRect.sizeDelta = new Vector2(60f, 20f);
            label = labelObject.AddComponent<TextMeshProUGUI>();
            label.text = "Squad #1";
            label.horizontalAlignment = HorizontalAlignmentOptions.Center;
            return row;
        }

        private static void AssertLeftAlignedSquadRow(
            RectTransform row,
            RectTransform icon,
            TMP_Text label)
        {
            Bounds iconBounds = BoundsIn(row, icon);
            Bounds labelBounds = BoundsIn(row, label.rectTransform);
            Assert.That(label.horizontalAlignment, Is.EqualTo(HorizontalAlignmentOptions.Left));
            Assert.That(labelBounds.min.x, Is.GreaterThan(iconBounds.max.x));
            Assert.That(labelBounds.max.x, Is.LessThanOrEqualTo(row.rect.xMax + 0.02f));
        }

        private static Bounds BoundsIn(RectTransform owner, RectTransform child)
        {
            return RectTransformUtility.CalculateRelativeRectTransformBounds(owner, child);
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