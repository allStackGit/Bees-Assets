using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ReportedGameplayRegressionTests
    {
        [Test]
        public void PooledStaticObstaclesReceiveStageLifecycleBeforePathfinderSetup()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "StaticObstaclePool.cs"));

            Assert.That(source, Does.Contain("obstacle.Create(_stage);"),
                "New pooled obstacles must run the same Stage initialization required by Obstacle.Setup.");
            Assert.That(source, Does.Contain("obstacle.Stage = _stage;"),
                "Reused pooled obstacles must retain explicit Stage ownership.");
        }

        [Test]
        public void EnemyRightClickRoutesBargeOnlySquadsThroughCharge()
        {
            string interaction = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Interaction.cs"));
            string targeting = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.UserTargeting.cs"));

            Assert.That(interaction, Does.Contain("selectedSquad.UserTargetEnemy(Squad)"));
            Assert.That(targeting, Does.Contain("HasOnlyBarges"));
            Assert.That(targeting, Does.Contain("ConfigData.CommandTypes.Charge"));
            Assert.That(targeting, Does.Contain("((Charge)GetCommand()).Execute"));
            Assert.That(targeting, Does.Contain("UserAggressive(enemy);"),
                "Non-Barge squads must keep the ordinary targeting path.");
        }

        [Test]
        public void GenericDialogueBlocksRaycastsAcrossTheWholeCanvas()
        {
            string dialogue = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "Dialogue.cs"));
            string blocker = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "ModalInputBlocker.cs"));

            Assert.That(dialogue, Does.Contain("ModalInputBlocker.Ensure(_dialogue);"));
            Assert.That(blocker, Does.Contain("rect.anchorMin = Vector2.zero;"));
            Assert.That(blocker, Does.Contain("rect.anchorMax = Vector2.one;"));
            Assert.That(blocker, Does.Contain("rect.SetSiblingIndex(0);"));
            Assert.That(blocker, Does.Contain("image.raycastTarget = true;"));
        }

        [Test]
        public void CommanderNamePromptRepairsInactiveLegacyControlsWhenShown()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "CommanderNamePromptGuard.cs"));

            Assert.That(source, Does.Contain("private void OnEnable()"));
            Assert.That(source, Does.Contain("ModalInputBlocker.Ensure(gameObject);"));
            Assert.That(source, Does.Contain("Welcome Commander!"));
            Assert.That(source, Does.Contain("Choose a commander name."));
            Assert.That(source, Does.Contain("mainMenu.NameInput"));
            Assert.That(source, Does.Contain("ActivateBranch(input.transform);"));
            Assert.That(source, Does.Contain("input.interactable = true;"));
            Assert.That(source, Does.Contain("label.text = \"Confirm\";"));
        }
    }
}
