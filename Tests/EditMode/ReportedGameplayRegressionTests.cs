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
        public void CommanderNamePromptKeepsItsInputVisibleFocusableAndAuthoritative()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "CommanderNamePromptGuard.cs"));

            Assert.That(source, Does.Contain("private void OnEnable()"));
            Assert.That(source, Does.Contain("ModalInputBlocker.Ensure(gameObject);"));
            Assert.That(source, Does.Contain("Welcome Commander!"));
            Assert.That(source, Does.Contain("Choose a commander name."));
            Assert.That(source, Does.Contain("GetComponentInChildren<TMP_InputField>(true)"),
                "The visible modal must resolve its own text field rather than trust a stale scene reference.");
            Assert.That(source, Does.Contain("layoutElement.ignoreLayout = true;"),
                "The Text area's VerticalLayoutGroup must not move the commander input underneath the Buttons panel.");
            Assert.That(source, Does.Contain("inputRect.anchoredPosition = InputPosition;"));
            Assert.That(source, Does.Contain("inputRect.sizeDelta = InputSize;"));
            Assert.That(source, Does.Contain("targetGraphic.color = InputBackground;"));
            Assert.That(source, Does.Contain("input.interactable = true;"));
            Assert.That(source, Does.Contain("input.readOnly = false;"));
            Assert.That(source, Does.Contain("input.ActivateInputField();"));
            Assert.That(source, Does.Contain("mainMenu.NameInput = input;"),
                "SubmitName must read from the same field that is visible in the modal.");
            Assert.That(source, Does.Contain("label.text = \"Confirm\";"));
        }

        [Test]
        public void CommanderNamePromptRepairsConfirmButtonAndSubmitBinding()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "CommanderNamePromptGuard.cs"));

            Assert.That(source, Does.Contain("button.enabled = true;"),
                "A disabled Button component can remain visually normal while ignoring pointer input.");
            Assert.That(source, Does.Contain("button.interactable = true;"));
            Assert.That(source, Does.Contain("buttonGraphic.enabled = true;"));
            Assert.That(source, Does.Contain("buttonGraphic.raycastTarget = true;"),
                "The confirm graphic must participate in UI raycasting.");
            Assert.That(source, Does.Contain("HasPersistentSubmitListener(button, mainMenu)"),
                "A valid serialized SubmitName callback should not be duplicated.");
            Assert.That(source, Does.Contain("button.onClick.AddListener(mainMenu.SubmitName);"),
                "Legacy prompt instances with a stale persistent target need a runtime SubmitName fallback.");
        }

        [Test]
        public void CommanderNameSubmitPersistsTheTypedPlayerName()
        {
            string mainMenu = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenu.cs"));

            int submit = mainMenu.IndexOf("public void SubmitName()");
            int nextMethod = mainMenu.IndexOf("public void ShowMenuPanel()", submit);
            Assert.That(submit, Is.GreaterThanOrEqualTo(0));
            Assert.That(nextMethod, Is.GreaterThan(submit));

            string submitBody = mainMenu.Substring(submit, nextMethod - submit);
            Assert.That(submitBody, Does.Contain("string name = NameInput.text;"));
            Assert.That(submitBody, Does.Contain("ConfigData.UserProgressData.PlayerName = name;"));
            Assert.That(submitBody, Does.Contain("ConfigData.UserProgressData.Save();"));
        }
    }
}
