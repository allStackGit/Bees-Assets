using System.IO;
using NUnit.Framework;
using UnityEngine;

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

            Assert.That(source, Does.Contain("WidthMultiplier = 1.25f"));
            Assert.That(source, Does.Contain("HorizontalPadding = 22f"));
            Assert.That(source, Does.Contain("VerticalPadding = 18f"));
            Assert.That(source, Does.Contain("_authoredFontSize + 2f"));
            Assert.That(source, Does.Contain("EstimateReadableWidth"));
            Assert.That(source, Does.Contain("Tutorial Close Hit Area"));
            Assert.That(source, Does.Contain("button.onClick.AddListener(Hide)"));
            Assert.That(source, Does.Contain("Tutorial Info Tab"));
            Assert.That(source, Does.Contain("UnityEngine.UI.Outline"));
            Assert.That(source, Does.Contain("Input.GetKeyDown(KeyCode.Space)"));
            Assert.That(source, Does.Contain("_previousButton"));
            Assert.That(source, Does.Contain("_nextButton"));
            Assert.That(source, Does.Contain("_sequenceIndex + 1"));
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
