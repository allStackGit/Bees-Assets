using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerSerializedLayoutContractTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.SquadMakerResponsiveLayoutGuard";
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);

        [Test]
        public void TrackedSceneContainsTheNativeHierarchyThatResponsiveLayoutOwns()
        {
            string scene = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scenes",
                "Squad Maker.unity"));
            string mainPanelPrefab = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Prefabs",
                "UI",
                "MainPanel.prefab"));

            // These are the real serialized regions used by the production ChosenSquadList ancestry
            // path. This guard prevents a synthetic-only fixture from silently drifting away from the
            // scene that players actually load.
            Assert.That(scene, Does.Contain("value: Main Container"));
            Assert.That(scene, Does.Contain("value: Ship Selector Column"));
            Assert.That(scene, Does.Contain("value: Squad Maker Column"));
            Assert.That(scene, Does.Contain("value: Squad Settings"));
            Assert.That(scene, Does.Contain("value: Squad Composition"));
            Assert.That(scene, Does.Contain("value: Squads Column"));
            Assert.That(scene, Does.Contain("value: Saved Squads Column"));
            Assert.That(scene, Does.Contain("value: Chosen Squads Column"));

            // Main Container, Squad Maker Column, and Squads Column are Panel prefab instances whose
            // scene-owned layouts begin with manual child sizing. The runtime owner deliberately
            // changes those native layout contracts rather than repeatedly writing layout-owned
            // RectTransforms. The center column's authored 298+420 split is especially important:
            // both panels must consume the live width, while the composition region absorbs vertical
            // surplus instead of leaving the orange parent visible between/below fixed children.
            Assert.That(scene, Does.Contain("guid: dd037183c8013734eae4f02aeab00941"));
            Assert.That(scene, Does.Contain("guid: 30649d3a9faa99c48a7b1166b86bf2a0"));
            Assert.That(scene, Does.Contain("m_ChildControlWidth: 0"));
            Assert.That(scene, Does.Contain("m_ChildControlHeight: 0"));
            Assert.That(scene, Does.Contain("value: 298"));
            Assert.That(scene, Does.Contain("value: 420"));

            // The shared MainPanel prefab really does contribute gutters that the old synthetic
            // fixture omitted. Squad Maker's specialized layout owner must normalize these at runtime.
            Assert.That(mainPanelPrefab, Does.Contain("m_Left: 5"));
            Assert.That(mainPanelPrefab, Does.Contain("m_Right: 5"));
            Assert.That(mainPanelPrefab, Does.Contain("m_Top: 5"));
            Assert.That(mainPanelPrefab, Does.Contain("m_Bottom: 5"));
            Assert.That(mainPanelPrefab, Does.Contain("m_Spacing: 10"));
        }

        [Test]
        public void AuthoredHierarchyUsesNativeLayoutsAndTilesViewportAcrossRepeatedAspectChanges()
        {
            RectTransform canvas = CreateRect("Canvas", null, ReferenceResolution);
            RectTransform mainPanel = CreateRect("MainPanel", canvas, Vector2.zero);
            mainPanel.anchorMin = Vector2.zero;
            mainPanel.anchorMax = Vector2.one;
            mainPanel.anchoredPosition = Vector2.zero;
            mainPanel.sizeDelta = Vector2.zero;

            // Match MainPanel.prefab itself. The real prefab starts with 5px padding and 10px spacing;
            // the Squad Maker owner must remove those inherited decorative gutters before tiling.
            VerticalLayoutGroup panelLayout = mainPanel.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.padding = new RectOffset(5, 5, 5, 5);
            panelLayout.spacing = 10f;
            panelLayout.childAlignment = TextAnchor.UpperLeft;
            panelLayout.childControlWidth = false;
            panelLayout.childControlHeight = false;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;

            RectTransform mainContainer = CreateRect("Main Container", mainPanel, new Vector2(1366f, 718f));
            HorizontalLayoutGroup mainLayout = mainContainer.gameObject.AddComponent<HorizontalLayoutGroup>();
            mainLayout.padding = new RectOffset(0, 0, 0, 0);
            mainLayout.spacing = 0f;
            mainLayout.childAlignment = TextAnchor.UpperLeft;
            mainLayout.childControlWidth = false;
            mainLayout.childControlHeight = false;
            mainLayout.childForceExpandWidth = true;
            mainLayout.childForceExpandHeight = true;

            RectTransform footer = CreateRect("Footer", mainPanel, new Vector2(1366f, 51f));
            RectTransform shipSelector = CreateRect("Ship Selector Column", mainContainer, new Vector2(262f, 718f));
            RectTransform squadMaker = CreateRect("Squad Maker Column", mainContainer, new Vector2(620f, 718f));
            RectTransform squads = CreateRect("Squads Column", mainContainer, new Vector2(484f, 718f));

            // Match the real Squad Maker Column Panel prefab. Its two direct panels are authored at
            // 620x298 and 620x420. Leaving childControl* false while force-expand is true allocates
            // surplus without resizing the visual panels, which is the orange-gap regression.
            VerticalLayoutGroup squadMakerLayout = squadMaker.gameObject.AddComponent<VerticalLayoutGroup>();
            squadMakerLayout.padding = new RectOffset(0, 0, 0, 0);
            squadMakerLayout.spacing = 0f;
            squadMakerLayout.childAlignment = TextAnchor.UpperLeft;
            squadMakerLayout.childControlWidth = false;
            squadMakerLayout.childControlHeight = false;
            squadMakerLayout.childForceExpandWidth = true;
            squadMakerLayout.childForceExpandHeight = true;

            RectTransform squadSettings = CreateRect("Squad Settings", squadMaker, new Vector2(620f, 298f));
            RectTransform squadComposition = CreateRect("Squad Composition", squadMaker, new Vector2(620f, 420f));

            HorizontalLayoutGroup squadsLayout = squads.gameObject.AddComponent<HorizontalLayoutGroup>();
            squadsLayout.padding = new RectOffset(0, 0, 0, 0);
            squadsLayout.spacing = 0f;
            squadsLayout.childAlignment = TextAnchor.UpperLeft;
            squadsLayout.childControlWidth = false;
            squadsLayout.childControlHeight = false;
            squadsLayout.childForceExpandWidth = true;
            squadsLayout.childForceExpandHeight = true;

            RectTransform savedSquads = CreateRect("Saved Squads Column", squads, new Vector2(262f, 718f));
            RectTransform chosenSquads = CreateRect("Chosen Squads Column", squads, new Vector2(222f, 718f));

            Component guard = canvas.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                RuntimeAssembly.SetField(guard, "_canvasRect", canvas);
                RuntimeAssembly.Invoke(guard, "CaptureDirectReferenceBranches");
                RuntimeAssembly.SetField(guard, "_referenceGeometryCaptured", true);

                ApplyAndAssertCoverage(
                    guard,
                    canvas,
                    ReferenceResolution,
                    mainPanel,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squadSettings,
                    squadComposition,
                    squads,
                    savedSquads,
                    chosenSquads,
                    expectedCenterWidth: 620f,
                    expectedBodyHeight: 717f,
                    expectedCompositionHeight: 419f);

                AssertNativeOwnership(
                    panelLayout,
                    mainLayout,
                    squadMakerLayout,
                    squadsLayout,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squadSettings,
                    squadComposition,
                    squads,
                    savedSquads,
                    chosenSquads);

                ApplyAndAssertCoverage(
                    guard,
                    canvas,
                    new Vector2(2000f, 768f),
                    mainPanel,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squadSettings,
                    squadComposition,
                    squads,
                    savedSquads,
                    chosenSquads,
                    expectedCenterWidth: 1254f,
                    expectedBodyHeight: 717f,
                    expectedCompositionHeight: 419f);

                // Periodic repair at the same aspect must remain idempotent.
                ApplyAndAssertCoverage(
                    guard,
                    canvas,
                    new Vector2(2000f, 768f),
                    mainPanel,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squadSettings,
                    squadComposition,
                    squads,
                    savedSquads,
                    chosenSquads,
                    expectedCenterWidth: 1254f,
                    expectedBodyHeight: 717f,
                    expectedCompositionHeight: 419f);

                ApplyAndAssertCoverage(
                    guard,
                    canvas,
                    new Vector2(1366f, 1000f),
                    mainPanel,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squadSettings,
                    squadComposition,
                    squads,
                    savedSquads,
                    chosenSquads,
                    expectedCenterWidth: 620f,
                    expectedBodyHeight: 949f,
                    expectedCompositionHeight: 651f);

                // Revisit the previous aspect, then return to reference, to catch stateful drift.
                ApplyAndAssertCoverage(
                    guard,
                    canvas,
                    new Vector2(2000f, 768f),
                    mainPanel,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squadSettings,
                    squadComposition,
                    squads,
                    savedSquads,
                    chosenSquads,
                    expectedCenterWidth: 1254f,
                    expectedBodyHeight: 717f,
                    expectedCompositionHeight: 419f);

                ApplyAndAssertCoverage(
                    guard,
                    canvas,
                    ReferenceResolution,
                    mainPanel,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squadSettings,
                    squadComposition,
                    squads,
                    savedSquads,
                    chosenSquads,
                    expectedCenterWidth: 620f,
                    expectedBodyHeight: 717f,
                    expectedCompositionHeight: 419f);
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void ChosenSquadScrollHeightAddsOnlyLiveColumnSurplusToSemanticBaseHeight()
        {
            System.Type guardType = RuntimeAssembly.GetType(GuardTypeName);

            Assert.That(
                (float)RuntimeAssembly.InvokeStatic(guardType, "CalculateSurplusAbsorbingHeight", 278f, 718f, 717f),
                Is.EqualTo(278f).Within(0.01f));
            Assert.That(
                (float)RuntimeAssembly.InvokeStatic(guardType, "CalculateSurplusAbsorbingHeight", 278f, 718f, 949f),
                Is.EqualTo(509f).Within(0.01f));
            Assert.That(
                (float)RuntimeAssembly.InvokeStatic(guardType, "CalculateSurplusAbsorbingHeight", 415f, 718f, 949f),
                Is.EqualTo(646f).Within(0.01f));
            Assert.That(
                (float)RuntimeAssembly.InvokeStatic(guardType, "CalculateSurplusAbsorbingHeight", 663f, 718f, 949f),
                Is.EqualTo(894f).Within(0.01f));
        }

        private static void AssertNativeOwnership(
            VerticalLayoutGroup panelLayout,
            HorizontalLayoutGroup mainLayout,
            VerticalLayoutGroup squadMakerLayout,
            HorizontalLayoutGroup squadsLayout,
            RectTransform mainContainer,
            RectTransform footer,
            RectTransform shipSelector,
            RectTransform squadMaker,
            RectTransform squadSettings,
            RectTransform squadComposition,
            RectTransform squads,
            RectTransform savedSquads,
            RectTransform chosenSquads)
        {
            Assert.That(panelLayout.padding.left, Is.Zero);
            Assert.That(panelLayout.padding.right, Is.Zero);
            Assert.That(panelLayout.padding.top, Is.Zero);
            Assert.That(panelLayout.padding.bottom, Is.Zero);
            Assert.That(panelLayout.spacing, Is.Zero);
            Assert.That(panelLayout.childAlignment, Is.EqualTo(TextAnchor.UpperLeft));
            Assert.That(panelLayout.childControlWidth, Is.True);
            Assert.That(panelLayout.childControlHeight, Is.True);
            Assert.That(panelLayout.childForceExpandWidth, Is.True);
            Assert.That(panelLayout.childForceExpandHeight, Is.False);

            Assert.That(mainLayout.padding.left, Is.Zero);
            Assert.That(mainLayout.padding.right, Is.Zero);
            Assert.That(mainLayout.padding.top, Is.Zero);
            Assert.That(mainLayout.padding.bottom, Is.Zero);
            Assert.That(mainLayout.spacing, Is.Zero);
            Assert.That(mainLayout.childAlignment, Is.EqualTo(TextAnchor.UpperLeft));
            Assert.That(mainLayout.childControlWidth, Is.True);
            Assert.That(mainLayout.childControlHeight, Is.True);
            Assert.That(mainLayout.childForceExpandWidth, Is.False);
            Assert.That(mainLayout.childForceExpandHeight, Is.True);

            Assert.That(squadMakerLayout.padding.left, Is.Zero);
            Assert.That(squadMakerLayout.padding.right, Is.Zero);
            Assert.That(squadMakerLayout.padding.top, Is.Zero);
            Assert.That(squadMakerLayout.padding.bottom, Is.Zero);
            Assert.That(squadMakerLayout.spacing, Is.Zero);
            Assert.That(squadMakerLayout.childAlignment, Is.EqualTo(TextAnchor.UpperLeft));
            Assert.That(squadMakerLayout.childControlWidth, Is.True);
            Assert.That(squadMakerLayout.childControlHeight, Is.True);
            Assert.That(squadMakerLayout.childForceExpandWidth, Is.True);
            Assert.That(squadMakerLayout.childForceExpandHeight, Is.False);

            Assert.That(squadsLayout.padding.left, Is.Zero);
            Assert.That(squadsLayout.padding.right, Is.Zero);
            Assert.That(squadsLayout.padding.top, Is.Zero);
            Assert.That(squadsLayout.padding.bottom, Is.Zero);
            Assert.That(squadsLayout.spacing, Is.Zero);
            Assert.That(squadsLayout.childAlignment, Is.EqualTo(TextAnchor.UpperLeft));
            Assert.That(squadsLayout.childControlWidth, Is.True);
            Assert.That(squadsLayout.childControlHeight, Is.True);
            Assert.That(squadsLayout.childForceExpandWidth, Is.False);
            Assert.That(squadsLayout.childForceExpandHeight, Is.True);

            AssertLayoutElement(mainContainer, flexibleWidth: 1f, flexibleHeight: 1f);
            AssertLayoutElement(footer, flexibleWidth: 1f, flexibleHeight: 0f);
            Assert.That(footer.GetComponent<LayoutElement>().preferredHeight, Is.EqualTo(51f).Within(0.01f));

            AssertLayoutElement(shipSelector, flexibleWidth: 0f, flexibleHeight: 1f);
            AssertLayoutElement(squadMaker, flexibleWidth: 1f, flexibleHeight: 1f);
            AssertLayoutElement(squadSettings, flexibleWidth: 1f, flexibleHeight: 0f);
            Assert.That(squadSettings.GetComponent<LayoutElement>().preferredHeight, Is.EqualTo(298f).Within(0.01f));
            AssertLayoutElement(squadComposition, flexibleWidth: 1f, flexibleHeight: 1f);
            Assert.That(squadComposition.GetComponent<LayoutElement>().preferredHeight, Is.EqualTo(420f).Within(0.01f));
            AssertLayoutElement(squads, flexibleWidth: 0f, flexibleHeight: 1f);
            AssertLayoutElement(savedSquads, flexibleWidth: 0f, flexibleHeight: 1f);
            AssertLayoutElement(chosenSquads, flexibleWidth: 0f, flexibleHeight: 1f);
        }

        private static void ApplyAndAssertCoverage(
            Component guard,
            RectTransform canvas,
            Vector2 canvasSize,
            RectTransform mainPanel,
            RectTransform mainContainer,
            RectTransform footer,
            RectTransform shipSelector,
            RectTransform squadMaker,
            RectTransform squadSettings,
            RectTransform squadComposition,
            RectTransform squads,
            RectTransform savedSquads,
            RectTransform chosenSquads,
            float expectedCenterWidth,
            float expectedBodyHeight,
            float expectedCompositionHeight)
        {
            canvas.sizeDelta = canvasSize;
            RuntimeAssembly.Invoke(guard, "ApplyViewportFill");

            AssertSize(mainPanel, canvasSize.x, canvasSize.y);
            AssertSize(mainContainer, canvasSize.x, expectedBodyHeight);
            AssertSize(footer, canvasSize.x, 51f);
            AssertSize(shipSelector, 262f, expectedBodyHeight);
            AssertSize(squadMaker, expectedCenterWidth, expectedBodyHeight);
            AssertSize(squadSettings, expectedCenterWidth, 298f);
            AssertSize(squadComposition, expectedCenterWidth, expectedCompositionHeight);
            AssertSize(squads, 484f, expectedBodyHeight);
            AssertSize(savedSquads, 262f, expectedBodyHeight);
            AssertSize(chosenSquads, 222f, expectedBodyHeight);

            AssertTilesHorizontally(mainContainer, shipSelector, squadMaker, squads);
            AssertTilesHorizontally(squads, savedSquads, chosenSquads);
            AssertTilesVertically(mainPanel, mainContainer, footer);
            AssertTilesVertically(squadMaker, squadSettings, squadComposition);
        }

        private static void AssertTilesHorizontally(RectTransform parent, params RectTransform[] children)
        {
            float expectedLeft = parent.rect.xMin;
            for (int i = 0; i < children.Length; i++)
            {
                Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(parent, children[i]);
                Assert.That(bounds.min.x, Is.EqualTo(expectedLeft).Within(0.01f),
                    children[i].name + " must start where the previous structural region ended");
                expectedLeft = bounds.max.x;
            }

            Assert.That(expectedLeft, Is.EqualTo(parent.rect.xMax).Within(0.01f),
                parent.name + " children must cover the full horizontal region without a gutter");
        }

        private static void AssertTilesVertically(
            RectTransform parent,
            RectTransform first,
            RectTransform second)
        {
            Bounds firstBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(parent, first);
            Bounds secondBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(parent, second);

            Assert.That(firstBounds.max.y, Is.EqualTo(parent.rect.yMax).Within(0.01f));
            Assert.That(firstBounds.min.y, Is.EqualTo(secondBounds.max.y).Within(0.01f),
                "Vertical structural regions must meet without exposing their parent backer.");
            Assert.That(secondBounds.min.y, Is.EqualTo(parent.rect.yMin).Within(0.01f));
        }

        private static void AssertLayoutElement(
            RectTransform rect,
            float flexibleWidth,
            float flexibleHeight)
        {
            LayoutElement element = rect.GetComponent<LayoutElement>();
            Assert.That(element, Is.Not.Null, rect.name + " must expose sizing intent to its LayoutGroup owner");
            Assert.That(element.ignoreLayout, Is.False);
            Assert.That(element.flexibleWidth, Is.EqualTo(flexibleWidth).Within(0.01f));
            Assert.That(element.flexibleHeight, Is.EqualTo(flexibleHeight).Within(0.01f));
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

        private static void AssertSize(RectTransform rect, float expectedWidth, float expectedHeight)
        {
            Assert.That(rect.rect.width, Is.EqualTo(expectedWidth).Within(0.01f), rect.name + " width");
            Assert.That(rect.rect.height, Is.EqualTo(expectedHeight).Within(0.01f), rect.name + " height");
        }
    }
}
