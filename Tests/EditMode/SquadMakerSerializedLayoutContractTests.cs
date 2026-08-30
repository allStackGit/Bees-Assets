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

            Assert.That(scene, Does.Contain("value: Main Container"));
            Assert.That(scene, Does.Contain("value: Ship Selector Column"));
            Assert.That(scene, Does.Contain("value: Squad Maker Column"));
            Assert.That(scene, Does.Contain("value: Squad Settings"));
            Assert.That(scene, Does.Contain("value: Squad Composition"));
            Assert.That(scene, Does.Contain("value: Squads Column"));
            Assert.That(scene, Does.Contain("value: Saved Squads Column"));
            Assert.That(scene, Does.Contain("value: Chosen Squads Column"));

            // The scene starts with legacy manual sizing. Responsive ownership deliberately converts
            // these structural regions into LayoutGroup/relative relationships at runtime.
            Assert.That(scene, Does.Contain("guid: dd037183c8013734eae4f02aeab00941"));
            Assert.That(scene, Does.Contain("guid: 30649d3a9faa99c48a7b1166b86bf2a0"));
            Assert.That(scene, Does.Contain("m_ChildControlWidth: 0"));
            Assert.That(scene, Does.Contain("m_ChildControlHeight: 0"));
            Assert.That(scene, Does.Contain("value: 298"));
            Assert.That(scene, Does.Contain("value: 420"));

            Assert.That(mainPanelPrefab, Does.Contain("m_Left: 5"));
            Assert.That(mainPanelPrefab, Does.Contain("m_Right: 5"));
            Assert.That(mainPanelPrefab, Does.Contain("m_Top: 5"));
            Assert.That(mainPanelPrefab, Does.Contain("m_Bottom: 5"));
            Assert.That(mainPanelPrefab, Does.Contain("m_Spacing: 10"));
        }

        [Test]
        public void AuthoredHierarchyUsesNativeRelationshipsAcrossIndependentWidthAndHeightChanges()
        {
            RectTransform canvas = CreateRect("Canvas", null, ReferenceResolution);
            RectTransform mainPanel = CreateRect("MainPanel", canvas, Vector2.zero);
            mainPanel.anchorMin = Vector2.zero;
            mainPanel.anchorMax = Vector2.one;
            mainPanel.anchoredPosition = Vector2.zero;
            mainPanel.sizeDelta = Vector2.zero;

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
            ConfigureLegacyManualLayout(mainLayout);

            RectTransform footer = CreateRect("Footer", mainPanel, new Vector2(1366f, 51f));
            RectTransform shipSelector = CreateRect("Ship Selector Column", mainContainer, new Vector2(262f, 718f));
            RectTransform squadMaker = CreateRect("Squad Maker Column", mainContainer, new Vector2(620f, 718f));
            RectTransform squads = CreateRect("Squads Column", mainContainer, new Vector2(484f, 718f));

            VerticalLayoutGroup shipSelectorLayout = shipSelector.gameObject.AddComponent<VerticalLayoutGroup>();
            ConfigureLegacyColumnLayout(shipSelectorLayout);
            RectTransform shipRow = CreateRect("Ship Row", shipSelector, new Vector2(262f, 30f));

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
            ConfigureLegacyManualLayout(squadsLayout);

            RectTransform savedSquads = CreateRect("Saved Squads Column", squads, new Vector2(262f, 718f));
            RectTransform chosenSquads = CreateRect("Chosen Squads Column", squads, new Vector2(222f, 718f));

            VerticalLayoutGroup savedLayout = savedSquads.gameObject.AddComponent<VerticalLayoutGroup>();
            ConfigureLegacyColumnLayout(savedLayout);
            RectTransform savedRow = CreateRect("Saved Row", savedSquads, new Vector2(262f, 30f));

            VerticalLayoutGroup chosenLayout = chosenSquads.gameObject.AddComponent<VerticalLayoutGroup>();
            ConfigureLegacyColumnLayout(chosenLayout);
            RectTransform chosenRow = CreateRect("Chosen Row", chosenSquads, new Vector2(222f, 30f));

            Component guard = canvas.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                RuntimeAssembly.SetField(guard, "_canvasRect", canvas);
                RuntimeAssembly.Invoke(guard, "CaptureDirectReferenceBranches");
                RuntimeAssembly.SetField(guard, "_referenceGeometryCaptured", true);

                Vector2[] liveLogicalSizes =
                {
                    ReferenceResolution,
                    new Vector2(1700f, 768f),
                    new Vector2(1366f, 960f),
                    new Vector2(2100f, 1080f),
                    new Vector2(3200f, 768f),
                    new Vector2(1700f, 768f),
                    ReferenceResolution
                };

                for (int index = 0; index < liveLogicalSizes.Length; index++)
                {
                    ApplyAndAssertCoverage(
                        guard,
                        canvas,
                        liveLogicalSizes[index],
                        mainPanel,
                        mainContainer,
                        footer,
                        shipSelector,
                        shipRow,
                        squadMaker,
                        squadSettings,
                        squadComposition,
                        squads,
                        savedSquads,
                        savedRow,
                        chosenSquads,
                        chosenRow);
                }

                AssertNativeOwnership(
                    panelLayout,
                    mainLayout,
                    shipSelectorLayout,
                    squadMakerLayout,
                    squadsLayout,
                    savedLayout,
                    chosenLayout,
                    mainContainer,
                    footer,
                    shipSelector,
                    squadMaker,
                    squadSettings,
                    squadComposition,
                    squads,
                    savedSquads,
                    chosenSquads);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void ChosenSquadScrollKeepsSemanticBaseWhileAbsorbingOnlyLiveColumnSurplus()
        {
            RectTransform chosenSquads = CreateRect(
                "Chosen Squads Column",
                null,
                new Vector2(222f, 718f));
            RectTransform chosenScroll = CreateRect(
                "Chosen Squads Scroll",
                chosenSquads,
                new Vector2(222f, 278f));
            Component guard = chosenSquads.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                System.Type referenceType = guard.GetType().GetNestedType(
                    "SquadMakerLayoutReferenceGeometry",
                    System.Reflection.BindingFlags.NonPublic);
                Assert.That(referenceType, Is.Not.Null);

                object reference = System.Activator.CreateInstance(referenceType);
                SetReferenceField(referenceType, reference, "ChosenSquadsColumn", chosenSquads);
                SetReferenceField(referenceType, reference, "ChosenSquadScroll", chosenScroll);
                SetReferenceField(
                    referenceType,
                    reference,
                    "ChosenSquadsColumnSize",
                    new Vector2(222f, 718f));

                RuntimeAssembly.Invoke(guard, "ApplyChosenSquadScrollSurplus", reference);
                AssertSize(chosenScroll, 222f, 278f);

                chosenSquads.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 949f);
                RuntimeAssembly.Invoke(guard, "ApplyChosenSquadScrollSurplus", reference);
                AssertSize(chosenScroll, 222f, 509f);

                // Simulate the scene controller selecting a different semantic list state while tall.
                chosenScroll.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 415f);
                RuntimeAssembly.Invoke(guard, "ApplyChosenSquadScrollSurplus", reference);
                AssertSize(chosenScroll, 222f, 646f);

                chosenSquads.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 718f);
                RuntimeAssembly.Invoke(guard, "ApplyChosenSquadScrollSurplus", reference);
                AssertSize(chosenScroll, 222f, 415f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(chosenSquads.gameObject);
            }
        }

        [Test]
        public void LevelDetailsModeKeepsChosenSquadScrollAtSemanticBaseSoDetailsCanUseSurplus()
        {
            System.Type guardType = RuntimeAssembly.GetType(GuardTypeName);

            float detailsHeight = (float)RuntimeAssembly.InvokeStatic(
                guardType,
                "CalculateChosenSquadScrollHeight",
                278f,
                718f,
                949f,
                true);
            float ordinaryHeight = (float)RuntimeAssembly.InvokeStatic(
                guardType,
                "CalculateChosenSquadScrollHeight",
                278f,
                718f,
                949f,
                false);

            Assert.That(detailsHeight, Is.EqualTo(278f).Within(0.01f),
                "Level-details mode must leave tall-screen surplus for the details/report area.");
            Assert.That(ordinaryHeight, Is.EqualTo(509f).Within(0.01f),
                "Other semantic list states should continue using legitimate extra column height.");
        }

        private static void ConfigureLegacyManualLayout(HorizontalLayoutGroup layout)
        {
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
        }

        private static void ConfigureLegacyColumnLayout(VerticalLayoutGroup layout)
        {
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }

        private static void SetReferenceField(
            System.Type referenceType,
            object reference,
            string fieldName,
            object value)
        {
            System.Reflection.FieldInfo field = referenceType.GetField(fieldName);
            Assert.That(field, Is.Not.Null, fieldName + " must remain part of the responsive reference contract");
            field.SetValue(reference, value);
        }

        private static void AssertNativeOwnership(
            VerticalLayoutGroup panelLayout,
            HorizontalLayoutGroup mainLayout,
            VerticalLayoutGroup shipSelectorLayout,
            VerticalLayoutGroup squadMakerLayout,
            HorizontalLayoutGroup squadsLayout,
            VerticalLayoutGroup savedLayout,
            VerticalLayoutGroup chosenLayout,
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
            Assert.That(panelLayout.childControlWidth, Is.True);
            Assert.That(panelLayout.childControlHeight, Is.True);
            Assert.That(panelLayout.childForceExpandWidth, Is.True);
            Assert.That(panelLayout.childForceExpandHeight, Is.False);

            Assert.That(mainLayout.childControlWidth, Is.True);
            Assert.That(mainLayout.childControlHeight, Is.True);
            Assert.That(mainLayout.childForceExpandWidth, Is.False);
            Assert.That(mainLayout.childForceExpandHeight, Is.True);

            Assert.That(squadMakerLayout.childControlWidth, Is.True);
            Assert.That(squadMakerLayout.childControlHeight, Is.True);
            Assert.That(squadMakerLayout.childForceExpandWidth, Is.True);
            Assert.That(squadMakerLayout.childForceExpandHeight, Is.False);

            Assert.That(squadsLayout.childControlWidth, Is.True);
            Assert.That(squadsLayout.childControlHeight, Is.True);
            Assert.That(squadsLayout.childForceExpandWidth, Is.False);
            Assert.That(squadsLayout.childForceExpandHeight, Is.True);

            AssertColumnCrossAxisOwnership(shipSelectorLayout);
            AssertColumnCrossAxisOwnership(savedLayout);
            AssertColumnCrossAxisOwnership(chosenLayout);

            AssertLayoutElement(mainContainer, flexibleWidth: 1f, flexibleHeight: 1f);
            AssertLayoutElement(footer, flexibleWidth: 1f, flexibleHeight: 0f);
            Assert.That(footer.GetComponent<LayoutElement>().preferredHeight, Is.EqualTo(51f).Within(0.01f));

            AssertLayoutElement(shipSelector, flexibleWidth: 262f, flexibleHeight: 1f);
            AssertLayoutElement(squadMaker, flexibleWidth: 620f, flexibleHeight: 1f);
            AssertLayoutElement(squadSettings, flexibleWidth: 1f, flexibleHeight: 0f);
            Assert.That(squadSettings.GetComponent<LayoutElement>().preferredHeight, Is.EqualTo(298f).Within(0.01f));
            AssertLayoutElement(squadComposition, flexibleWidth: 1f, flexibleHeight: 1f);
            Assert.That(squadComposition.GetComponent<LayoutElement>().preferredHeight, Is.EqualTo(420f).Within(0.01f));
            AssertLayoutElement(squads, flexibleWidth: 484f, flexibleHeight: 1f);
            AssertLayoutElement(savedSquads, flexibleWidth: 262f, flexibleHeight: 1f);
            AssertLayoutElement(chosenSquads, flexibleWidth: 222f, flexibleHeight: 1f);
        }

        private static void AssertColumnCrossAxisOwnership(VerticalLayoutGroup layout)
        {
            Assert.That(layout.childControlWidth, Is.True);
            Assert.That(layout.childForceExpandWidth, Is.True);
        }

        private static void ApplyAndAssertCoverage(
            Component guard,
            RectTransform canvas,
            Vector2 canvasSize,
            RectTransform mainPanel,
            RectTransform mainContainer,
            RectTransform footer,
            RectTransform shipSelector,
            RectTransform shipRow,
            RectTransform squadMaker,
            RectTransform squadSettings,
            RectTransform squadComposition,
            RectTransform squads,
            RectTransform savedSquads,
            RectTransform savedRow,
            RectTransform chosenSquads,
            RectTransform chosenRow)
        {
            canvas.sizeDelta = canvasSize;
            RuntimeAssembly.Invoke(guard, "ApplyViewportFill");

            float horizontalScale = canvasSize.x / ReferenceResolution.x;
            float expectedBodyHeight = canvasSize.y - 51f;
            float expectedCompositionHeight = expectedBodyHeight - 298f;
            float expectedShipSelectorWidth = 262f * horizontalScale;
            float expectedCenterWidth = 620f * horizontalScale;
            float expectedSquadsWidth = 484f * horizontalScale;
            float expectedSavedSquadsWidth = 262f * horizontalScale;
            float expectedChosenSquadsWidth = 222f * horizontalScale;

            AssertSize(mainPanel, canvasSize.x, canvasSize.y);
            AssertSize(mainContainer, canvasSize.x, expectedBodyHeight);
            AssertSize(footer, canvasSize.x, 51f);
            AssertSize(shipSelector, expectedShipSelectorWidth, expectedBodyHeight);
            AssertSize(shipRow, expectedShipSelectorWidth, 30f);
            AssertSize(squadMaker, expectedCenterWidth, expectedBodyHeight);
            AssertSize(squadSettings, expectedCenterWidth, 298f);
            AssertSize(squadComposition, expectedCenterWidth, expectedCompositionHeight);
            AssertSize(squads, expectedSquadsWidth, expectedBodyHeight);
            AssertSize(savedSquads, expectedSavedSquadsWidth, expectedBodyHeight);
            AssertSize(savedRow, expectedSavedSquadsWidth, 30f);
            AssertSize(chosenSquads, expectedChosenSquadsWidth, expectedBodyHeight);
            AssertSize(chosenRow, expectedChosenSquadsWidth, 30f);

            AssertTilesHorizontally(mainContainer, shipSelector, squadMaker, squads);
            AssertTilesHorizontally(squads, savedSquads, chosenSquads);
            AssertTilesVertically(mainPanel, mainContainer, footer);
            AssertTilesVertically(squadMaker, squadSettings, squadComposition);
        }

        private static void AssertTilesHorizontally(RectTransform parent, params RectTransform[] children)
        {
            float expectedLeft = parent.rect.xMin;
            for (int index = 0; index < children.Length; index++)
            {
                Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(parent, children[index]);
                Assert.That(bounds.min.x, Is.EqualTo(expectedLeft).Within(0.01f),
                    children[index].name + " must start where the previous structural region ended");
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
            Assert.That(firstBounds.min.y, Is.EqualTo(secondBounds.max.y).Within(0.01f));
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
