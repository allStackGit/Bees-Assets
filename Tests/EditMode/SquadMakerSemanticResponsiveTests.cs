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
        private const string LayoutTypeName = "Assets.Scripts.UI_Components.SquadMakerCompositionLayoutGuard";
        private const string GuardTypeName = "Assets.Scripts.UI_Components.SquadMakerResponsiveLayoutGuard";
        private const string SquadMakerTypeName = "Assets.Scripts.Scenes.SquadMaker";
        private const string TmpTextTypeName = "TMPro.TextMeshProUGUI";

        [Test]
        public void HeaderUsesFullWidthWithFlexibleNameFieldAndCompactEdgeControls()
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
            object reference = RuntimeAssembly.InvokeStatic(layoutType, "Capture", squadMaker, settings, composition);

            try
            {
                float referenceHeaderWidth = header.rect.width;
                float referenceNameWidth = name.rect.width;
                float referenceSupplyWidth = supply.rect.width;
                float referenceColorWidth = color.rect.width;
                float referenceCountWidth = count.rect.width;
                Bounds referenceSupply = BoundsIn(header, supply);
                Bounds referenceName = BoundsIn(header, name);
                Bounds referenceColor = BoundsIn(header, color);
                Bounds referenceCount = BoundsIn(header, count);
                float referenceSupplyNameGap = referenceName.min.x - referenceSupply.max.x;
                float referenceNameColorGap = referenceColor.min.x - referenceName.max.x;
                float referenceColorCountGap = referenceCount.min.x - referenceColor.max.x;
                float referenceLeftMargin = referenceSupply.min.x - header.rect.xMin;
                float referenceRightMargin = header.rect.xMax - referenceCount.max.x;

                float[] widths = { 620f, 930f, 1240f, 2400f, 500f, 775f, 620f };
                for (int i = 0; i < widths.Length; i++)
                {
                    composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, widths[i]);
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                    Bounds headerBounds = BoundsIn(composition, header);
                    Bounds supplyBounds = BoundsIn(header, supply);
                    Bounds nameBounds = BoundsIn(header, name);
                    Bounds colorBounds = BoundsIn(header, color);
                    Bounds countBounds = BoundsIn(header, count);
                    float liveHeaderWidth = Mathf.Max(0f, widths[i] - 20f);
                    float scale = Mathf.Min(1f, liveHeaderWidth / referenceHeaderWidth);

                    Assert.That(headerBounds.size.x, Is.EqualTo(liveHeaderWidth).Within(0.02f));
                    Assert.That(nameBounds.min.x - supplyBounds.max.x,
                        Is.EqualTo(referenceSupplyNameGap * scale).Within(0.02f));
                    Assert.That(colorBounds.min.x - nameBounds.max.x,
                        Is.EqualTo(referenceNameColorGap * scale).Within(0.02f));
                    Assert.That(countBounds.min.x - colorBounds.max.x,
                        Is.EqualTo(referenceColorCountGap * scale).Within(0.02f));

                    float leftMargin = referenceLeftMargin * scale;
                    float rightMargin = referenceRightMargin * scale;
                    Assert.That(supplyBounds.min.x,
                        Is.EqualTo(header.rect.xMin + leftMargin).Within(0.02f));
                    Assert.That(countBounds.max.x,
                        Is.EqualTo(header.rect.xMax - rightMargin).Within(0.02f),
                        "The visible toolbar should span the header instead of floating in its center.");

                    float expectedNameWidth;
                    if (liveHeaderWidth >= referenceHeaderWidth)
                    {
                        float fixedWidth = leftMargin + referenceSupplyWidth + referenceSupplyNameGap +
                            referenceNameColorGap + referenceColorWidth + referenceColorCountGap +
                            referenceCountWidth + rightMargin;
                        expectedNameWidth = liveHeaderWidth - fixedWidth;
                        Assert.That(supply.rect.width, Is.EqualTo(referenceSupplyWidth).Within(0.02f));
                        Assert.That(color.rect.width, Is.EqualTo(referenceColorWidth).Within(0.02f));
                        Assert.That(count.rect.width, Is.EqualTo(referenceCountWidth).Within(0.02f));
                    }
                    else
                    {
                        expectedNameWidth = referenceNameWidth * scale;
                        Assert.That(supply.rect.width, Is.EqualTo(referenceSupplyWidth * scale).Within(0.02f));
                        Assert.That(color.rect.width, Is.EqualTo(referenceColorWidth * scale).Within(0.02f));
                        Assert.That(count.rect.width, Is.EqualTo(referenceCountWidth * scale).Within(0.02f));
                    }

                    Assert.That(name.rect.width, Is.EqualTo(expectedNameWidth).Within(0.02f),
                        "Horizontal surplus should become useful squad-name editing space, not empty toolbar margins.");
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
        public void UltrawideMainColumnsKeepSideRailsAuthoredAndGiveSurplusToCenter()
        {
            RectTransform row = CreateRect("Main Container", null, new Vector2(1366f, 718f));
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            RectTransform inventory = CreateRect("Ship Selector Column", row, new Vector2(262f, 718f));
            RectTransform center = CreateRect("Squad Maker Column", row, new Vector2(620f, 718f));
            RectTransform squads = CreateRect("Squads Column", row, new Vector2(484f, 718f));
            Type guardType = RuntimeAssembly.GetType(GuardTypeName);

            try
            {
                RuntimeAssembly.InvokeStatic(guardType, "ConfigureFixedWidthFlexibleHeight", inventory, 262f);
                RuntimeAssembly.InvokeStatic(guardType, "ConfigureSurplusAbsorbingWidthFlexibleHeight", center, 620f);
                RuntimeAssembly.InvokeStatic(guardType, "ConfigureFixedWidthFlexibleHeight", squads, 484f);

                float[] widths = { 1366f, 2000f, 5278f, 1600f, 1366f };
                for (int i = 0; i < widths.Length; i++)
                {
                    row.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, widths[i]);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(row);

                    Assert.That(inventory.rect.width, Is.EqualTo(262f).Within(0.02f));
                    Assert.That(squads.rect.width, Is.EqualTo(484f).Within(0.02f));
                    Assert.That(center.rect.width, Is.EqualTo(widths[i] - 262f - 484f).Within(0.02f),
                        "Only the central Squad Maker workspace should absorb ultrawide horizontal surplus.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(row.gameObject);
            }
        }

        [Test]
        public void SquadRowsUseStableAuthoredIconSlotWithoutRepositioningSpriteArtwork()
        {
            RectTransform savedList = CreateRect("Saved List", null, new Vector2(262f, 300f));
            RectTransform chosenList = CreateRect("Chosen List", null, new Vector2(222f, 300f));
            RectTransform savedRow = CreateRuntimeSquadRow("Saved Row", savedList, 262f, "Pantheras", out RectTransform savedRuntime, out RectTransform savedGraphic, out RectTransform savedLegacy, out Component savedLabel, out HorizontalLayoutGroup savedLayout);
            RectTransform chosenRow = CreateRuntimeSquadRow("Chosen Row", chosenList, 222f, "Blue Squadron", out RectTransform chosenRuntime, out RectTransform chosenGraphic, out RectTransform chosenLegacy, out Component chosenLabel, out HorizontalLayoutGroup chosenLayout);

            Vector2 savedGraphicOffset = savedGraphic.anchoredPosition;
            Vector2 chosenGraphicOffset = chosenGraphic.anchoredPosition;
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "SavedSquadList", savedList.gameObject);
            RuntimeAssembly.SetField(squadMaker, "ChosenSquadList", chosenList.gameObject);
            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);
            object reference = RuntimeAssembly.InvokeStatic(layoutType, "Capture", squadMaker, settings, composition);

            try
            {
                RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);
                Assert.That(savedLayout.enabled, Is.False);
                Assert.That(chosenLayout.enabled, Is.False);
                Assert.That(savedLegacy.gameObject.activeSelf, Is.False);
                Assert.That(chosenLegacy.gameObject.activeSelf, Is.False);
                AssertIconSlot(savedRow, savedRuntime, savedLabel, 48f, savedLayout.spacing);
                AssertIconSlot(chosenRow, chosenRuntime, savedLabel == chosenLabel ? chosenLabel : chosenLabel, 48f, chosenLayout.spacing);
                Assert.That(savedGraphic.anchoredPosition, Is.EqualTo(savedGraphicOffset));
                Assert.That(chosenGraphic.anchoredPosition, Is.EqualTo(chosenGraphicOffset));

                Bounds savedSlotBaseline = BoundsIn(savedRow, savedRuntime);
                Bounds chosenSlotBaseline = BoundsIn(chosenRow, chosenRuntime);
                Bounds savedLabelBaseline = BoundsIn(savedRow, savedLabel.transform as RectTransform);
                Bounds chosenLabelBaseline = BoundsIn(chosenRow, chosenLabel.transform as RectTransform);
                for (int pass = 0; pass < 12; pass++)
                {
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);
                    AssertBoundsEqual(savedSlotBaseline, BoundsIn(savedRow, savedRuntime));
                    AssertBoundsEqual(chosenSlotBaseline, BoundsIn(chosenRow, chosenRuntime));
                    AssertBoundsEqual(savedLabelBaseline, BoundsIn(savedRow, savedLabel.transform as RectTransform));
                    AssertBoundsEqual(chosenLabelBaseline, BoundsIn(chosenRow, chosenLabel.transform as RectTransform));
                    Assert.That(savedGraphic.anchoredPosition, Is.EqualTo(savedGraphicOffset));
                    Assert.That(chosenGraphic.anchoredPosition, Is.EqualTo(chosenGraphicOffset));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(manager);
                UnityEngine.Object.DestroyImmediate(savedList.gameObject);
                UnityEngine.Object.DestroyImmediate(chosenList.gameObject);
                UnityEngine.Object.DestroyImmediate(settings.gameObject);
                UnityEngine.Object.DestroyImmediate(composition.gameObject);
            }
        }

        [Test]
        public void BlarpRailAndDropWorkspaceRemainSeparateAcrossArbitrarySizes()
        {
            RectTransform settings = CreateRect("Squad Settings", null, new Vector2(620f, 298f));
            RectTransform composition = CreateRect("Squad Composition", null, new Vector2(620f, 420f));
            RectTransform formations = CreateRect("Formations", composition, new Vector2(30f, 410f));
            formations.anchorMin = new Vector2(0f, 0.5f);
            formations.anchorMax = new Vector2(0f, 0.5f);
            RectTransform control = CreateRect("BLARP Button", formations, new Vector2(42f, 30f));
            control.anchoredPosition = new Vector2(-9f, 0f);
            control.gameObject.AddComponent<Image>();
            RectTransform dropZone = CreateRect("Drop Zone", composition, new Vector2(600f, 340f));
            dropZone.anchoredPosition = new Vector2(7f, -5f);

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
                for (int i = 0; i < sizes.Length; i++)
                {
                    composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, sizes[i].x);
                    composition.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, sizes[i].y);
                    RuntimeAssembly.InvokeStatic(layoutType, "Apply", reference);

                    Bounds rail = BoundsIn(composition, formations);
                    Bounds work = BoundsIn(composition, dropZone);
                    Bounds controlBounds = BoundsIn(formations, control);
                    Assert.That(rail.min.x, Is.GreaterThanOrEqualTo(composition.rect.xMin - 0.02f));
                    Assert.That(rail.max.x, Is.LessThanOrEqualTo(work.min.x - 3.9f));
                    Assert.That(work.max.x, Is.LessThanOrEqualTo(composition.rect.xMax + 0.02f));
                    Assert.That(controlBounds.min.x, Is.GreaterThanOrEqualTo(formations.rect.xMin - 0.02f));
                    Assert.That(controlBounds.max.x, Is.LessThanOrEqualTo(formations.rect.xMax + 0.02f));
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
            RectTransform child = CreateRect("Cursor", overlay, new Vector2(10f, 10f));
            child.anchoredPosition = new Vector2(-80f, -50f);
            Type layoutType = RuntimeAssembly.GetType(LayoutTypeName);

            try
            {
                RuntimeAssembly.InvokeStatic(layoutType, "PositionOverlayNearAnchor", viewport, anchor, overlay);
                Rect first = RectBounds(viewport, overlay);
                Rect anchorBounds = RectBounds(viewport, anchor);
                Assert.That(first.center.x, Is.EqualTo(anchorBounds.center.x).Within(0.02f));
                Assert.That(first.yMax, Is.EqualTo(anchorBounds.yMin - 4f).Within(0.02f));
                child.anchoredPosition = new Vector2(80f, -180f);
                RuntimeAssembly.InvokeStatic(layoutType, "PositionOverlayNearAnchor", viewport, anchor, overlay);
                Rect second = RectBounds(viewport, overlay);
                Assert.That(second.xMin, Is.EqualTo(first.xMin).Within(0.02f));
                Assert.That(second.yMin, Is.EqualTo(first.yMin).Within(0.02f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(viewport.gameObject);
            }
        }

        private static RectTransform CreateRuntimeSquadRow(string name, RectTransform parent, float width, string squadName, out RectTransform runtimeIcon, out RectTransform runtimeGraphic, out RectTransform legacyIcon, out Component label, out HorizontalLayoutGroup layout)
        {
            RectTransform row = CreateRect(name, parent, new Vector2(width, 32f));
            row.gameObject.AddComponent<Image>();
            layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 10f;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            legacyIcon = CreateRect("Squad Icon", row, new Vector2(48f, 32f));
            legacyIcon.gameObject.AddComponent<Image>();
            runtimeIcon = CreateRect("Icon Container", row, new Vector2(36f, 24f));
            runtimeGraphic = CreateRect("Ship Icon", runtimeIcon, new Vector2(20f, 20f));
            runtimeGraphic.anchoredPosition = new Vector2(7f, 3f);
            runtimeGraphic.gameObject.AddComponent<Image>();
            GameObject labelObject = new GameObject("Squad Name", typeof(RectTransform), typeof(CanvasRenderer));
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.SetParent(row, false);
            labelRect.sizeDelta = new Vector2(180f, 32f);
            label = labelObject.AddComponent(GetTmpTextType());
            SetTmpText(label, squadName);
            SetTmpHorizontalAlignment(label, "Center");
            return row;
        }

        private static void AssertIconSlot(RectTransform row, RectTransform icon, Component label, float slotWidth, float spacing)
        {
            Bounds iconBounds = BoundsIn(row, icon);
            Bounds labelBounds = BoundsIn(row, label.transform as RectTransform);
            Assert.That(iconBounds.min.x, Is.EqualTo(row.rect.xMin).Within(0.02f));
            Assert.That(iconBounds.size.x, Is.EqualTo(slotWidth).Within(0.02f));
            Assert.That(iconBounds.center.y, Is.EqualTo(row.rect.center.y).Within(0.02f));
            Assert.That(labelBounds.min.x, Is.EqualTo(row.rect.xMin + slotWidth + spacing).Within(0.02f));
            AssertTmpHorizontalAlignment(label, "Left");
        }

        private static RectTransform CreateTopRowRect(string name, RectTransform parent, float left, float width)
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
            if (parent != null) rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            return rect;
        }

        private static Type GetTmpTextType()
        {
            Type type = Type.GetType($"{TmpTextTypeName}, Unity.TextMeshPro");
            if (type != null) return type;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(TmpTextTypeName, false);
                if (type != null) return type;
            }
            throw new TypeLoadException($"Could not resolve loaded runtime type '{TmpTextTypeName}'.");
        }

        private static void SetTmpText(Component label, string text) => GetTmpProperty(label, "text").SetValue(label, text);
        private static void SetTmpHorizontalAlignment(Component label, string value)
        {
            PropertyInfo property = GetTmpProperty(label, "horizontalAlignment");
            property.SetValue(label, Enum.Parse(property.PropertyType, value));
        }
        private static void AssertTmpHorizontalAlignment(Component label, string expected)
        {
            object value = GetTmpProperty(label, "horizontalAlignment").GetValue(label);
            Assert.That(value?.ToString(), Is.EqualTo(expected));
        }
        private static PropertyInfo GetTmpProperty(Component component, string name)
        {
            PropertyInfo property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null);
            return property;
        }
        private static Bounds BoundsIn(RectTransform owner, RectTransform child) => RectTransformUtility.CalculateRelativeRectTransformBounds(owner, child);
        private static void AssertBoundsEqual(Bounds expected, Bounds actual)
        {
            Assert.That(actual.min.x, Is.EqualTo(expected.min.x).Within(0.02f));
            Assert.That(actual.max.x, Is.EqualTo(expected.max.x).Within(0.02f));
            Assert.That(actual.min.y, Is.EqualTo(expected.min.y).Within(0.02f));
            Assert.That(actual.max.y, Is.EqualTo(expected.max.y).Within(0.02f));
        }
        private static Rect RectBounds(RectTransform owner, RectTransform child)
        {
            Vector3[] corners = new Vector3[4];
            child.GetWorldCorners(corners);
            Vector3 first = owner.InverseTransformPoint(corners[0]);
            float minX = first.x, maxX = first.x, minY = first.y, maxY = first.y;
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 p = owner.InverseTransformPoint(corners[i]);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }
    }
}
