using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TutorialUiPolishContractTests
    {
        [Test]
        public void TutorialTooltipKeepsReadableSizingNavigationAndDeterministicCloseHitArea()
        {
            string source = ReadSource("Scripts", "UI Components", "Tooltip.cs");

            Assert.That(source, Does.Contain("WidthMultiplier = 1.1f"));
            Assert.That(source, Does.Contain("MaxReadableWidth = 500f"));
            Assert.That(source, Does.Contain("SequenceRequestedHeightMultiplier = 0.78f"));
            Assert.That(source, Does.Contain("HorizontalPadding = 22f"));
            Assert.That(source, Does.Contain("VerticalPadding = 18f"));
            Assert.That(source, Does.Contain("_authoredFontSize + 2f"));
            Assert.That(source, Does.Contain("EstimateReadableWidth"));
            Assert.That(source, Does.Contain("Tutorial Close Hit Area"));
            Assert.That(source, Does.Contain("button.onClick.AddListener(Hide)"));
            Assert.That(source, Does.Contain("Tutorial Info Tab"));
            Assert.That(source, Does.Contain("rect.anchoredPosition = new Vector2(-InfoTabBorder, 0f)"));
            Assert.That(source, Does.Contain("rect.sizeDelta = new Vector2(InfoTabWidth + InfoTabBorder, InfoTabHeight)"));
            Assert.That(source, Does.Contain("TutorialInfoTabGraphic"));
            Assert.That(source, Does.Contain("Input.GetKeyDown(KeyCode.Space)"));
            Assert.That(source, Does.Contain("_previousButton"));
            Assert.That(source, Does.Contain("_nextButton"));
            Assert.That(source, Does.Contain("_sequenceIndex + 1"));
        }

        [Test]
        public void TutorialCloseButtonPreservesAuthoredHoverColors()
        {
            GameObject tooltipObject = new GameObject("Tooltip");
            GameObject closeButtonObject = new GameObject(
                "Close Button",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));

            try
            {
                Component tooltip = tooltipObject.AddComponent(RuntimeAssembly.GetType("Tooltip"));
                RuntimeAssembly.SetField(tooltip, "CloseButton", closeButtonObject);

                Button button = closeButtonObject.GetComponent<Button>();
                Image image = closeButtonObject.GetComponent<Image>();
                ColorBlock authoredColors = button.colors;
                authoredColors.normalColor = new Color(0.9f, 0.9f, 0.9f, 1f);
                authoredColors.highlightedColor = new Color(0.25f, 0.25f, 0.25f, 1f);
                authoredColors.pressedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
                button.colors = authoredColors;

                System.Reflection.MethodInfo configureCloseButton = tooltip.GetType().GetMethod(
                    "ConfigureCloseButton",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(configureCloseButton, Is.Not.Null);

                configureCloseButton.Invoke(tooltip, null);

                Assert.That(button.targetGraphic, Is.SameAs(image));
                Assert.That(button.colors.normalColor, Is.EqualTo(authoredColors.normalColor));
                Assert.That(button.colors.highlightedColor, Is.EqualTo(authoredColors.highlightedColor));
                Assert.That(button.colors.pressedColor, Is.EqualTo(authoredColors.pressedColor));
            }
            finally
            {
                Object.DestroyImmediate(closeButtonObject);
                Object.DestroyImmediate(tooltipObject);
            }
        }

        [Test]
        public void TutorialInfoTabRightEdgeSlantsDownToPanelBorder()
        {
            System.Type graphicType = RuntimeAssembly.GetType("TutorialInfoTabGraphic");
            Vector2[] vertices = (Vector2[])RuntimeAssembly.InvokeStatic(
                graphicType,
                "CalculateInfoTabVertices",
                new Rect(0f, 0f, 90f, 30f),
                12f);

            Assert.That(vertices.Length, Is.EqualTo(4));
            Assert.That(vertices[0], Is.EqualTo(new Vector2(0f, 0f)));
            Assert.That(vertices[1], Is.EqualTo(new Vector2(90f, 0f)),
                "The tab bottom must run flush into the tutorial panel border.");
            Assert.That(vertices[2], Is.EqualTo(new Vector2(78f, 30f)),
                "The top-right corner must be inset so the right edge slopes downward/right.");
            Assert.That(vertices[3], Is.EqualTo(new Vector2(0f, 30f)),
                "The tab left edge must stay exactly flush with the panel's left edge.");
        }

        [Test]
        public void ActiveLevelDialogueStaysAboveTutorialWithoutReorderingOtherPopups()
        {
            GameObject overlay = new GameObject("UI Overlay");
            GameObject dialogueObject = new GameObject("Dialogue");
            dialogueObject.transform.SetParent(overlay.transform, false);
            dialogueObject.AddComponent(RuntimeAssembly.GetType("DialogueManager"));

            GameObject tooltipObject = new GameObject("Tooltip");
            tooltipObject.transform.SetParent(overlay.transform, false);
            Component tooltip = tooltipObject.AddComponent(RuntimeAssembly.GetType("Tooltip"));
            RuntimeAssembly.SetField(tooltip, "TooltipObject", tooltipObject);

            GameObject unrelatedPopup = new GameObject("Unrelated Popup");
            unrelatedPopup.transform.SetParent(overlay.transform, false);

            try
            {
                Assert.That(
                    tooltipObject.transform.GetSiblingIndex(),
                    Is.GreaterThan(dialogueObject.transform.GetSiblingIndex()));

                System.Reflection.MethodInfo keepBelowDialogue = tooltip.GetType().GetMethod(
                    "KeepBelowActiveDialogue",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(keepBelowDialogue, Is.Not.Null);

                keepBelowDialogue.Invoke(tooltip, null);

                Assert.That(
                    tooltipObject.transform.GetSiblingIndex(),
                    Is.LessThan(dialogueObject.transform.GetSiblingIndex()));
                Assert.That(
                    unrelatedPopup.transform.GetSiblingIndex(),
                    Is.GreaterThan(dialogueObject.transform.GetSiblingIndex()));
            }
            finally
            {
                Object.DestroyImmediate(overlay);
            }
        }

        [Test]
        public void PlutoTwoTutorialPagesGateEnemySpawnUntilTutorialAndDialogueAreComplete()
        {
            string source = ReadSource("Scripts", "Levels", "Level.Campaign.Pluto.cs");
            int plutoTwo = source.IndexOf("public void Pluto2Reinforcements()", System.StringComparison.Ordinal);
            Assert.That(plutoTwo, Is.GreaterThanOrEqualTo(0));

            int tutorial = source.IndexOf("basicTooltip.ShowSequence", plutoTwo, System.StringComparison.Ordinal);
            int combatGate = source.IndexOf(
                "Stage.CutsceneManager.HitDialogueBreak && tacticalTutorialComplete",
                tutorial,
                System.StringComparison.Ordinal);
            int enemySpawn = source.IndexOf(
                "ConfigData.ShipTypes.Honeybee, 1",
                combatGate,
                System.StringComparison.Ordinal);

            Assert.That(tutorial, Is.GreaterThan(plutoTwo));
            Assert.That(combatGate, Is.GreaterThan(tutorial));
            Assert.That(enemySpawn, Is.GreaterThan(combatGate));
            Assert.That(source, Does.Contain("holding <b>R</b>."));
            Assert.That(source, Does.Contain("pressing <b>F</b>."));
            Assert.That(source, Does.Contain("(the exclamation point)"));
            Assert.That(source, Does.Not.Contain("(the red exclamation point)"));
        }

        [Test]
        public void PlutoFourFleetTutorialUsesSequenceBeforeCombatStarts()
        {
            string source = ReadSource("Scripts", "Levels", "Level.Campaign.Pluto4.cs");
            int mission = source.IndexOf("public void Pluto4BluerPasturesCampaign()", System.StringComparison.Ordinal);
            Assert.That(mission, Is.GreaterThanOrEqualTo(0));

            int tutorial = source.IndexOf("basicTooltip.ShowSequence", mission, System.StringComparison.Ordinal);
            int tutorialComplete = source.IndexOf("hasSeenFleetMessages = true", tutorial, System.StringComparison.Ordinal);
            int combatGate = source.IndexOf("() => hasSeenFleetMessages", tutorialComplete, System.StringComparison.Ordinal);
            int enemySpawn = source.IndexOf("AddReinforcementSquads(firstSquads", combatGate, System.StringComparison.Ordinal);

            Assert.That(tutorial, Is.GreaterThan(mission));
            Assert.That(tutorialComplete, Is.GreaterThan(tutorial));
            Assert.That(combatGate, Is.GreaterThan(tutorialComplete));
            Assert.That(enemySpawn, Is.GreaterThan(combatGate));
        }

        [Test]
        public void SquadMakerDeleteShortcutUsesDeleteAndExistingConfirmationFlow()
        {
            string source = ReadSource("Scripts", "Scenes", "SquadMakerDeleteShortcutGuard.cs");

            Assert.That(source, Does.Contain("Input.GetKeyDown(KeyCode.Delete)"));
            Assert.That(source, Does.Not.Contain("KeyCode.Backspace"));
            Assert.That(source, Does.Contain("_squadMaker.ConfirmDeleteSquad()"));
            Assert.That(source, Does.Contain("TMP_InputField"));
        }

        [Test]
        public void MissionSummaryKeepsEnemyLabelAndPerTypePlayerLossBreakdown()
        {
            string polish = ReadSource("Scripts", "UI Components", "GameUiPolishGuard.cs");
            string state = ReadSource("Scripts", "Levels", "GameState.cs");
            string combat = ReadSource("Scripts", "Entities", "Ships", "Ship.Combat.cs");

            Assert.That(polish, Does.Contain("Enemy Ships Destroyed:"));
            Assert.That(polish, Does.Contain("BuildShipsLostText"));
            Assert.That(polish, Does.Contain("fontSize *= 1.18f"));
            Assert.That(state, Does.Contain("PlayerShipsLostByType"));
            Assert.That(state, Does.Contain("RecordPlayerShipLost"));
            Assert.That(combat, Does.Contain("RecordPlayerShipLost(ShipType)"));
        }

        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < pathParts.Length; i++)
            {
                path = Path.Combine(path, pathParts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}