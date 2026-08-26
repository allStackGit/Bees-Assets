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
        public void SharedHeaderOwnerKeepsAuthoredControlStripBoundedOnWideLayouts()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform header = CreateRect("Header Row", composition, new Vector2(600f, 30f));
            header.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, 10f, 600f);
            header.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, 0f, 30f);

            RectTransform supply = CreateTopRowRect("Supply Capacity", header, 0f, 200f);
            RectTransform name = CreateTopRowRect("Squad Name", header, 210f, 220f);
            RectTransform color = CreateTopRowRect("COLOR", header, 440f, 70f);
            RectTransform count = CreateTopRowRect("0 / 10", header, 520f, 70f);

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
                Assert.That(reference, Is.Not.Null);
                float referenceSupplyWidth = supply.rect.width;
                float referenceNameWidth = name.rect.width;
                float referenceColorWidth = color.rect.width;
                float referenceCountWidth = count.rect.width;
                float referenceHeaderWidth = header.rect.width;
                Bounds referenceCountBounds = BoundsIn(header, count);
                float referenceRightMargin = header.rect.xMax - referenceCountBounds.max.x;

                float[] widths = { 620f, 930f, 1240f, 500f, 775f, 620f };
                for (int index = 0; index < widths.Length; index++)
                {
                    float width = widths[index];
                    composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                    Bounds headerBounds = BoundsIn(composition, header);
                    Bounds supplyBounds = BoundsIn(header, supply);
                    Bounds nameBounds = BoundsIn(header, name);
                    Bounds colorBounds = BoundsIn(header, color);
                    Bounds countBounds = BoundsIn(header, count);

                    float expectedHeaderWidth = Mathf.Max(0f, width - 20f);
                    float contentWidth = Mathf.Min(referenceHeaderWidth, expectedHeaderWidth);
                    float scale = referenceHeaderWidth > 0f
                        ? Mathf.Min(1f, contentWidth / referenceHeaderWidth)
                        : 1f;

                    Assert.That(headerBounds.size.x, Is.EqualTo(expectedHeaderWidth).Within(0.02f));
                    Assert.That(supplyBounds.max.x, Is.LessThanOrEqualTo(nameBounds.min.x + 0.02f));
                    Assert.That(nameBounds.max.x, Is.LessThanOrEqualTo(colorBounds.min.x + 0.02f));
                    Assert.That(colorBounds.max.x, Is.LessThanOrEqualTo(countBounds.min.x + 0.02f));
                    Assert.That(supplyBounds.min.x, Is.GreaterThanOrEqualTo(header.rect.xMin - 0.02f));
                    Assert.That(countBounds.max.x, Is.LessThanOrEqualTo(header.rect.xMax + 0.02f));

                    Assert.That(
                        supply.rect.width,
                        Is.EqualTo(referenceSupplyWidth * scale).Within(0.02f));
                    Assert.That(
                        name.rect.width,
                        Is.EqualTo(referenceNameWidth * scale).Within(0.02f),
                        "The squad-name input must retain its authored width on wide layouts.");
                    Assert.That(
                        color.rect.width,
                        Is.EqualTo(referenceColorWidth * scale).Within(0.02f));
                    Assert.That(
                        count.rect.width,
                        Is.EqualTo(referenceCountWidth * scale).Within(0.02f));
                    Assert.That(
                        countBounds.max.x,
                        Is.EqualTo(
                            header.rect.xMin + contentWidth - (referenceRightMargin * scale))
                            .Within(0.02f),
                        "Wide-layout surplus belongs to the composition region, not the name input.");
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
        public void DynamicSquadRowsUseAuthoredIconSlotAndDoNotJitterAcrossRepeatedRepairs()
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
                out RectTransform savedRuntimeGraphic,
                out RectTransform savedLegacyIcon,
                out Component savedLabel,
                out HorizontalLayoutGroup savedCompetingLayout);
            RectTransform chosenRow = CreateRuntimeSquadRow(
                "Chosen Row",
                chosenList,
                222f,
                "Blue Squadron",
                out RectTransform chosenRuntimeIcon,
                out RectTransform chosenRuntimeGraphic,
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
                // Reproduce the runtime state after Unity has already let the authored row layout see
                // both its legacy placeholder and the newly inserted runtime icon container.
                LayoutRebuilder.ForceRebuildLayoutImmediate(savedRow);
                LayoutRebuilder.ForceRebuildLayoutImmediate(chosenRow);
                RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                Assert.That(savedCompetingLayout.enabled, Is.False);
                Assert.That(chosenCompetingLayout.enabled, Is.False);
                Assert.That(savedLegacyIcon.gameObject.activeSelf, Is.False);
                Assert.That(chosenLegacyIcon.gameObject.activeSelf, Is.False);
                AssertStableSquadRow(
                    savedRow,
                    savedRuntimeGraphic,
                    savedLegacyIcon,
                    savedLabel,
                    savedCompetingLayout);
                AssertStableSquadRow(
                    chosenRow,
                    chosenRuntimeGraphic,
                    chosenLegacyIcon,
                    chosenLabel,
                    chosenCompetingLayout);

                Bounds savedIconBaseline = BoundsIn(savedRow, savedRuntimeGraphic);
                Bounds savedLabelBaseline = BoundsIn(savedRow, savedLabel.transform as RectTransform);
                Bounds chosenIconBaseline = BoundsIn(chosenRow, chosenRuntimeGraphic);
                Bounds chosenLabelBaseline = BoundsIn(chosenRow, chosenLabel.transform as RectTransform);

                for (int pass = 0; pass < 12; pass++)
                {
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);
                    AssertBoundsEqual(savedIconBaseline, BoundsIn(savedRow, savedRuntimeGraphic));
                    AssertBoundsEqual(savedLabelBaseline, BoundsIn(savedRow, savedLabel.transform as RectTransform));
                    AssertBoundsEqual(chosenIconBaseline, BoundsIn(chosenRow, chosenRuntimeGraphic));
                    AssertBoundsEqual(chosenLabelBaseline, BoundsIn(chosenRow, chosenLabel.transform as RectTransform));
                }

                savedColumn.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 420f);
                chosenColumn.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 360f);
                RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                Bounds savedIconWide = BoundsIn(savedRow, savedRuntimeGraphic);
                Bounds savedLabelWide = BoundsIn(savedRow, savedLabel.transform as RectTransform);
                Bounds chosenIconWide = BoundsIn(chosenRow, chosenRuntimeGraphic);
                Bounds chosenLabelWide = BoundsIn(chosenRow, chosenLabel.transform as RectTransform);

                Assert.That(savedRow.rect.width, Is.EqualTo(savedColumn.rect.width).Within(0.02f));
                Assert.That(chosenRow.rect.width, Is.EqualTo(chosenColumn.rect.width).Within(0.02f));
                Assert.That(savedIconWide.center.x, Is.EqualTo(savedIconBaseline.center.x).Within(0.02f));
                Assert.That(savedIconWide.center.y, Is.EqualTo(savedIconBaseline.center.y).Within(0.02f));
                Assert.That(savedLabelWide.min.x, Is.EqualTo(savedLabelBaseline.min.x).Within(0.02f));
                Assert.That(savedLabelWide.max.x, Is.GreaterThan(savedLabelBaseline.max.x));
                Assert.That(chosenIconWide.center.x, Is.EqualTo(chosenIconBaseline.center.x).Within(0.02f));
                Assert.That(chosenIconWide.center.y, Is.EqualTo(chosenIconBaseline.center.y).Within(0.02f));
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
        public void VisibleBlarpGraphicsRemainInsideAllDropWorkspaceEdges()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform formations = CreateRect("Formations", composition, new Vector2(30f, 410f));
            formations.anchorMin = new Vector2(0f, 0.5f);
            formations.anchorMax = new Vector2(0f, 0.5f);
            RectTransform protruding = CreateRect("BLARP Visual", formations, new Vector2(48f, 70f));
            protruding.anchoredPosition = new Vector2(-20f, 190f);
            protruding.gameObject.AddComponent<Image>();

            RectTransform dropZone = CreateRect("Drop Zone", composition, new Vector2(580f, 340f));
            dropZone.anchoredPosition = new Vector2(15f, -5f);

            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "DropZone", dropZone.gameObject);
            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(layoutType, "Capture", squadMaker, settings, composition);

            try
            {
                Vector2[] sizes =
                {
                    new Vector2(620f, 420f),
                    new Vector2(500f, 360f),
                    new Vector2(930f, 540f),
                    new Vector2(420f, 320f),
                    new Vector2(1240f, 620f),
                    new Vector2(620f, 420f)
                };

                Bounds baseline = default;
                for (int index = 0; index < sizes.Length; index++)
                {
                    composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, sizes[index].x);
                    composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, sizes[index].y);
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                    Bounds work = BoundsIn(composition, dropZone);
                    Bounds blarp = BoundsIn(composition, protruding);
                    Assert.That(blarp.min.x, Is.GreaterThanOrEqualTo(work.min.x - 0.02f));
                    Assert.That(blarp.max.x, Is.LessThanOrEqualTo(work.max.x + 0.02f));
                    Assert.That(blarp.min.y, Is.GreaterThanOrEqualTo(work.min.y - 0.02f));
                    Assert.That(blarp.max.y, Is.LessThanOrEqualTo(work.max.y + 0.02f));

                    if (index == 0)
                    {
                        baseline = blarp;
                    }
                    else if (index == sizes.Length - 1)
                    {
                        AssertBoundsEqual(baseline, blarp);
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
            out RectTransform runtimeGraphic,
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

            // Match the serialized Saved Squad Label contract: a fixed 48-wide authored icon slot.
            legacyIcon = CreateRect("Squad Icon", row, new Vector2(48f, 64f));
            legacyIcon.gameObject.AddComponent<Image>();

            // Deliberately make the runtime container/graphic a different size and offset its graphic.
            // The repair must align the rendered ship, not whichever container happened to be cloned.
            runtimeIcon = CreateRect("Icon Container", row, new Vector2(36f, 44f));
            runtimeGraphic = CreateRect("Ship Icon", runtimeIcon, new Vector2(20f, 26f));
            runtimeGraphic.anchoredPosition = new Vector2(7f, 8f);
            runtimeGraphic.gameObject.AddComponent<Image>();

            GameObject labelObject = new GameObject("Squad Name", typeof(RectTransform), typeof(CanvasRenderer));
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.SetParent(row, false);
            labelRect.sizeDelta = new Vector2(240f, 64f);
            label = labelObject.AddComponent(GetTmpTextType());
            SetTmpText(label, squadName);
            SetTmpHorizontalAlignment(label, "Center");
            return row;
        }

        private static void AssertStableSquadRow(
            RectTransform row,
            RectTransform visibleIcon,
            RectTransform legacyIcon,
            Component label,
            HorizontalLayoutGroup authoredLayout)
        {
            RectTransform labelRect = label.transform as RectTransform;
            Bounds iconBounds = BoundsIn(row, visibleIcon);
            Bounds legacyBounds = BoundsIn(row, legacyIcon);
            Bounds labelBounds = BoundsIn(row, labelRect);
            float expectedIconCenterX =
                row.rect.xMin + authoredLayout.padding.left + (legacyBounds.size.x * 0.5f);
            float expectedLabelMinX =
                row.rect.xMin + authoredLayout.padding.left + legacyBounds.size.x + authoredLayout.spacing;

            AssertTmpHorizontalAlignment(label, "Left");
            Assert.That(iconBounds.center.x, Is.EqualTo(expectedIconCenterX).Within(0.02f));
            Assert.That(iconBounds.center.y, Is.EqualTo(row.rect.center.y).Within(0.02f));
            Assert.That(labelBounds.min.x, Is.EqualTo(expectedLabelMinX).Within(0.02f));
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
