using System;
using System.Reflection;
using NUnit.Framework;
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
        private const string TmpTextTypeName = "TMPro.TextMeshProUGUI";

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

            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
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

                float[] widths = { 620f, 930f, 1240f, 500f, 775f, 620f };
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
        public void DynamicSquadRowsHaveOneGeometryOwnerAndDoNotJitterAcrossRepeatedRepairs()
        {
            RectTransform mainContainer = CreateRect("Main Container", null, new Vector2(1366f, 718f));
            RectTransform squadsColumn = CreateRect("Squads Column", mainContainer, new Vector2(484f, 718f));
            RectTransform savedColumn = CreateRect("Saved Squads Column", squadsColumn, new Vector2(262f, 718f));
            RectTransform chosenColumn = CreateRect("Chosen Squads Column", squadsColumn, new Vector2(222f, 718f));
            RectTransform savedList = CreateRect("Saved List", savedColumn, new Vector2(262f, 300f));
            RectTransform chosenList = CreateRect("Chosen List", chosenColumn, new Vector2(222f, 300f));

            RectTransform savedRow = CreateRuntimeSquadRow(
                "Saved Row",
                savedList,
                262f,
                "1st Pantheras",
                out RectTransform savedRuntimeIcon,
                out RectTransform savedLegacyIcon,
                out Component savedLabel,
                out HorizontalLayoutGroup savedCompetingLayout);
            RectTransform chosenRow = CreateRuntimeSquadRow(
                "Chosen Row",
                chosenList,
                222f,
                "Blue Squadron",
                out RectTransform chosenRuntimeIcon,
                out RectTransform chosenLegacyIcon,
                out Component chosenLabel,
                out HorizontalLayoutGroup chosenCompetingLayout);

            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "SavedSquadList", savedList.gameObject);
            RuntimeAssembly.SetField(squadMaker, "ChosenSquadList", chosenList.gameObject);

            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(
                layoutType,
                "Capture",
                squadMaker,
                settings,
                composition);

            try
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(savedRow);
                LayoutRebuilder.ForceRebuildLayoutImmediate(chosenRow);
                RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                Assert.That(savedCompetingLayout.enabled, Is.False);
                Assert.That(chosenCompetingLayout.enabled, Is.False);
                Assert.That(savedLegacyIcon.gameObject.activeSelf, Is.False);
                Assert.That(chosenLegacyIcon.gameObject.activeSelf, Is.False);
                AssertStableLeftAlignedSquadRow(savedRow, savedRuntimeIcon, savedLabel);
                AssertStableLeftAlignedSquadRow(chosenRow, chosenRuntimeIcon, chosenLabel);

                Bounds savedIconBaseline = BoundsIn(savedRow, savedRuntimeIcon);
                Bounds savedLabelBaseline = BoundsIn(savedRow, savedLabel.transform as RectTransform);
                Bounds chosenIconBaseline = BoundsIn(chosenRow, chosenRuntimeIcon);
                Bounds chosenLabelBaseline = BoundsIn(chosenRow, chosenLabel.transform as RectTransform);

                for (int pass = 0; pass < 12; pass++)
                {
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);
                    AssertBoundsEqual(savedIconBaseline, BoundsIn(savedRow, savedRuntimeIcon));
                    AssertBoundsEqual(savedLabelBaseline, BoundsIn(savedRow, savedLabel.transform as RectTransform));
                    AssertBoundsEqual(chosenIconBaseline, BoundsIn(chosenRow, chosenRuntimeIcon));
                    AssertBoundsEqual(chosenLabelBaseline, BoundsIn(chosenRow, chosenLabel.transform as RectTransform));
                }

                // Rows are layout-owned by their lists/columns. Widen the owners, then verify that
                // the stable left edge remains fixed while only the label's available right edge grows.
                savedColumn.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 420f);
                chosenColumn.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 360f);
                RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                Bounds savedIconWide = BoundsIn(savedRow, savedRuntimeIcon);
                Bounds savedLabelWide = BoundsIn(savedRow, savedLabel.transform as RectTransform);
                Bounds chosenIconWide = BoundsIn(chosenRow, chosenRuntimeIcon);
                Bounds chosenLabelWide = BoundsIn(chosenRow, chosenLabel.transform as RectTransform);

                Assert.That(savedRow.rect.width, Is.EqualTo(savedColumn.rect.width).Within(0.02f));
                Assert.That(chosenRow.rect.width, Is.EqualTo(chosenColumn.rect.width).Within(0.02f));
                Assert.That(savedIconWide.min.x, Is.EqualTo(savedIconBaseline.min.x).Within(0.02f));
                Assert.That(savedLabelWide.min.x, Is.EqualTo(savedLabelBaseline.min.x).Within(0.02f));
                Assert.That(savedLabelWide.max.x, Is.GreaterThan(savedLabelBaseline.max.x));
                Assert.That(chosenIconWide.min.x, Is.EqualTo(chosenIconBaseline.min.x).Within(0.02f));
                Assert.That(chosenLabelWide.min.x, Is.EqualTo(chosenLabelBaseline.min.x).Within(0.02f));
                Assert.That(chosenLabelWide.max.x, Is.GreaterThan(chosenLabelBaseline.max.x));
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
            Component heading = headingRect.gameObject.AddComponent(GetTmpTextType());
            SetTmpText(heading, "Squad Presets");

            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(layoutType, "Capture", null, settings, composition);

            try
            {
                settings.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 1240f);
                RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                Assert.That(info.rect.width, Is.EqualTo(settings.rect.width).Within(0.02f));
                Assert.That(backer.rect.width, Is.EqualTo(info.rect.width).Within(0.02f));
                Bounds headingBounds = BoundsIn(settings, headingRect);
                Assert.That(headingBounds.center.x, Is.EqualTo(settings.rect.center.x).Within(0.02f));
                AssertTmpHorizontalAlignment(heading, "Center");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings.gameObject);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        [Test]
        public void ActiveShipAndSquadInfoOverlaysFillTheLiveSettingsRegion()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            VerticalLayoutGroup settingsLayout = settings.gameObject.AddComponent<VerticalLayoutGroup>();
            settingsLayout.childControlWidth = false;
            settingsLayout.childForceExpandWidth = false;

            RectTransform normalPresetSurface = CreateRect("Preset Surface", settings, new Vector2(620f, 268f));
            normalPresetSurface.gameObject.AddComponent<Image>();
            RectTransform shipOverlay = CreateRect("Ship Info Box", settings, new Vector2(500f, 268f));
            shipOverlay.gameObject.AddComponent<Image>();
            RectTransform squadOverlay = CreateRect("Squad Info Box", settings, new Vector2(500f, 268f));
            squadOverlay.gameObject.AddComponent<Image>();

            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "ShipInfoBox", shipOverlay.gameObject);
            RuntimeAssembly.SetField(squadMaker, "SquadInfoBox", squadOverlay.gameObject);

            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(layoutType, "Capture", squadMaker, settings, composition);

            try
            {
                float[] widths = { 620f, 930f, 1240f, 775f, 620f };
                for (int index = 0; index < widths.Length; index++)
                {
                    settings.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, widths[index]);
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                    Assert.That(normalPresetSurface.rect.width, Is.EqualTo(settings.rect.width).Within(0.02f));
                    Assert.That(shipOverlay.rect.width, Is.EqualTo(settings.rect.width).Within(0.02f));
                    Assert.That(squadOverlay.rect.width, Is.EqualTo(settings.rect.width).Within(0.02f));
                    Assert.That(shipOverlay.GetComponent<LayoutElement>().ignoreLayout, Is.True);
                    Assert.That(squadOverlay.GetComponent<LayoutElement>().ignoreLayout, Is.True);
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
            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(layoutType, "Capture", squadMaker, settings, composition);

            try
            {
                float[] widths = { 620f, 500f, 930f, 420f, 1240f, 620f };
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

        [Test]
        public void OverlayRootRemainsAttachedToAnchorWhenDescendantGeometryChanges()
        {
            RectTransform viewport = CreateRect("Canvas", null, new Vector2(1000f, 700f));
            RectTransform anchor = CreateRect("COLOR", viewport, new Vector2(80f, 30f));
            anchor.anchoredPosition = new Vector2(100f, 150f);
            RectTransform overlay = CreateRect("Color Picker", viewport, new Vector2(220f, 260f));
            RectTransform movingDescendant = CreateRect("Mouse Indicator", overlay, new Vector2(10f, 10f));
            movingDescendant.anchoredPosition = new Vector2(-80f, -50f);

            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            try
            {
                RuntimeAssembly.InvokeStatic(layoutType, "PositionOverlayNearAnchor", viewport, anchor, overlay);
                Rect firstRoot = RectBounds(viewport, overlay);
                Rect anchorBounds = RectBounds(viewport, anchor);
                Assert.That(firstRoot.center.x, Is.EqualTo(anchorBounds.center.x).Within(0.02f));
                Assert.That(firstRoot.yMax, Is.EqualTo(anchorBounds.yMin - 4f).Within(0.02f));

                movingDescendant.anchoredPosition = new Vector2(80f, -180f);
                RuntimeAssembly.InvokeStatic(layoutType, "PositionOverlayNearAnchor", viewport, anchor, overlay);
                Rect secondRoot = RectBounds(viewport, overlay);
                Assert.That(secondRoot.xMin, Is.EqualTo(firstRoot.xMin).Within(0.02f));
                Assert.That(secondRoot.yMin, Is.EqualTo(firstRoot.yMin).Within(0.02f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(viewport.gameObject);
            }
        }

        private static RectTransform CreateTopRowRect(string name, RectTransform parent, float left, float width)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(width, 30f));
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, left, width);
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 0f, 30f);
            return rect;
        }

        private static RectTransform CreateRuntimeSquadRow(
            string name,
            RectTransform parent,
            float width,
            string squadName,
            out RectTransform runtimeIcon,
            out RectTransform legacyIcon,
            out Component label,
            out HorizontalLayoutGroup competingLayout)
        {
            RectTransform row = CreateRect(name, parent, new Vector2(width, 32f));
            row.gameObject.AddComponent<Image>();
            competingLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            competingLayout.padding = new RectOffset(0, 0, 0, 0);
            competingLayout.spacing = 10f;
            competingLayout.childAlignment = TextAnchor.MiddleCenter;
            competingLayout.childControlWidth = false;
            competingLayout.childControlHeight = false;
            competingLayout.childForceExpandWidth = false;
            competingLayout.childForceExpandHeight = false;

            legacyIcon = CreateRect("Squad Icon", row, new Vector2(30f, 30f));
            legacyIcon.gameObject.AddComponent<Image>();

            runtimeIcon = CreateRect("Icon Container", row, new Vector2(30f, 30f));
            RectTransform runtimeShipImage = CreateRect("Ship Icon", runtimeIcon, new Vector2(24f, 24f));
            runtimeShipImage.gameObject.AddComponent<Image>();

            GameObject labelObject = new GameObject("Squad Name", typeof(RectTransform), typeof(CanvasRenderer));
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.SetParent(row, false);
            labelRect.sizeDelta = new Vector2(Mathf.Max(60f, width - 50f), 30f);
            label = labelObject.AddComponent(GetTmpTextType());
            SetTmpText(label, squadName);
            SetTmpHorizontalAlignment(label, "Center");
            return row;
        }

        private static void AssertStableLeftAlignedSquadRow(RectTransform row, RectTransform icon, Component label)
        {
            RectTransform labelRect = label.transform as RectTransform;
            Bounds iconBounds = BoundsIn(row, icon);
            Bounds labelBounds = BoundsIn(row, labelRect);
            AssertTmpHorizontalAlignment(label, "Left");
            Assert.That(iconBounds.min.x, Is.GreaterThanOrEqualTo(row.rect.xMin - 0.02f));
            Assert.That(labelBounds.min.x, Is.GreaterThan(iconBounds.max.x));
            Assert.That(labelBounds.max.x, Is.LessThanOrEqualTo(row.rect.xMax + 0.02f));
        }

        private static void AssertBoundsEqual(Bounds expected, Bounds actual)
        {
            Assert.That(actual.min.x, Is.EqualTo(expected.min.x).Within(0.02f));
            Assert.That(actual.max.x, Is.EqualTo(expected.max.x).Within(0.02f));
            Assert.That(actual.min.y, Is.EqualTo(expected.min.y).Within(0.02f));
            Assert.That(actual.max.y, Is.EqualTo(expected.max.y).Within(0.02f));
        }

        private static Type GetTmpTextType()
        {
            Type type = Type.GetType($"{TmpTextTypeName}, Unity.TextMeshPro");
            if (type != null)
            {
                return type;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int index = 0; index < assemblies.Length; index++)
            {
                type = assemblies[index].GetType(TmpTextTypeName, false);
                if (type != null)
                {
                    return type;
                }
            }
            throw new TypeLoadException($"Could not resolve loaded runtime type '{TmpTextTypeName}'.");
        }

        private static void SetTmpText(Component label, string text)
        {
            SetTmpProperty(label, "text", text);
        }

        private static void SetTmpHorizontalAlignment(Component label, string alignmentName)
        {
            PropertyInfo property = GetTmpProperty(label, "horizontalAlignment");
            object alignment = Enum.Parse(property.PropertyType, alignmentName);
            property.SetValue(label, alignment);
        }

        private static void AssertTmpHorizontalAlignment(Component label, string expectedName)
        {
            PropertyInfo property = GetTmpProperty(label, "horizontalAlignment");
            object actual = property.GetValue(label);
            Assert.That(actual != null ? actual.ToString() : null, Is.EqualTo(expectedName));
        }

        private static void SetTmpProperty(Component component, string propertyName, object value)
        {
            GetTmpProperty(component, propertyName).SetValue(component, value);
        }

        private static PropertyInfo GetTmpProperty(Component component, string propertyName)
        {
            Assert.That(component, Is.Not.Null);
            PropertyInfo property = component.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, $"Expected TMP property '{propertyName}'.");
            return property;
        }

        private static Bounds BoundsIn(RectTransform owner, RectTransform child)
        {
            return RectTransformUtility.CalculateRelativeRectTransformBounds(owner, child);
        }

        private static Rect RectBounds(RectTransform owner, RectTransform child)
        {
            Vector3[] corners = new Vector3[4];
            child.GetWorldCorners(corners);
            Vector3 first = owner.InverseTransformPoint(corners[0]);
            float minX = first.x;
            float maxX = first.x;
            float minY = first.y;
            float maxY = first.y;
            for (int index = 1; index < corners.Length; index++)
            {
                Vector3 local = owner.InverseTransformPoint(corners[index]);
                minX = Mathf.Min(minX, local.x);
                maxX = Mathf.Max(maxX, local.x);
                minY = Mathf.Min(minY, local.y);
                maxY = Mathf.Max(maxY, local.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
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
