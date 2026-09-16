using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class InitialDesignPresentationRegressionTests
    {
        [Test]
        public void ReplayTooltipChoiceOnlyAppearsAtCampaignStart()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenu.cs"));

            int confirm = source.IndexOf("public void ConfirmPlayCampaign()");
            int nextMethod = source.IndexOf("public void DisableTooltips()", confirm);
            Assert.That(confirm, Is.GreaterThanOrEqualTo(0));
            Assert.That(nextMethod, Is.GreaterThan(confirm));

            string body = source.Substring(confirm, nextMethod - confirm);
            Assert.That(body, Does.Contain("currentCampaignLevel == 0"));
            Assert.That(body, Does.Contain("ConfigData.UserProgressData.HasPlayedBefore"));
            Assert.That(body, Does.Contain("isReplayingCampaignStart && ConfigData.UserProgressData.ShowToolTips"));
        }

        [Test]
        public void PlutoTwoTutorialCentersPresentationAndDoesNotOverlapDialogue()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "PlutoTwoTutorialPresentationGuard.cs"));

            Assert.That(source, Does.Contain("CenterMissionStatus(stage);"));
            Assert.That(source, Does.Contain("tooltip.TooltipPosition.localPosition = Vector3.zero;"));
            Assert.That(source, Does.Contain("Tutorial Sequence Footer"));
            Assert.That(source, Does.Contain("dialogueManager.DialogueBox.SetActive(false);"));
            Assert.That(source, Does.Contain("dialogueManager.enabled = false;"));
            Assert.That(source, Does.Contain("_heldDialogueManager.DialogueBox.SetActive(true);"),
                "Samuel's queued dialogue must become visible only after the tutorial sequence has closed.");
        }

        [Test]
        public void PlutoTwoHighlightUsesDimensionsInsteadOfGiantTransformScale()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "PlutoTwoTutorialPresentationGuard.cs"));

            Assert.That(source, Does.Contain("Mathf.Abs(scale.x - 150f)"));
            Assert.That(source, Does.Contain("Mathf.Abs(scale.y - 30f)"));
            Assert.That(source, Does.Contain("rect.localScale = Vector3.one;"));
            Assert.That(source, Does.Contain("rect.sizeDelta = new Vector2(150f, 30f);"));
        }

        [Test]
        public void SummaryNextButtonIsKeptInsideSummaryPanelAtCompactSize()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "MissionSummaryNextButtonLayoutGuard.cs"));

            Assert.That(source, Does.Contain("new Vector2(116f, 30f)"));
            Assert.That(source, Does.Contain("new Vector2(0f, 9f)"));
            Assert.That(source, Does.Contain("rect.localScale = Vector3.one;"));
        }

        [Test]
        public void SquadMakerBrochureMatchesTooltipPaletteAndTracksColorButton()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "Brochure.cs"));

            Assert.That(source, Does.Contain("ApplyTooltipPresentation();"));
            Assert.That(source, Does.Contain("AddColorButtonArrow();"));
            Assert.That(source, Does.Contain("_squadMaker.SquadColorPickerButton"));
            Assert.That(source, Does.Contain("ScreenPointToLocalPointInRectangle"),
                "The color callout must derive its canvas position from the actual responsive button.");
            Assert.That(source, Does.Contain("label.text = \"INFO\";"));
        }

        [Test]
        public void UnsavedWorkingSquadKeepsItsFleetShipsReserved()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "SquadMakerFeedbackAdjustmentGuard.cs"));

            Assert.That(source, Does.Contain("SavedSquad workingSquad = _squadMaker.GetCurrentSquad();"));
            Assert.That(source, Does.Contain("List<SquadShip> workingShips = workingSquad.GetSquadShips();"));
            Assert.That(source, Does.Contain("referencedFleetIds.Add(workingShips[shipIndex].FleetId);"),
                "Ships in the unsaved editor squad must not be returned to the available fleet by orphan repair.");
        }

        [Test]
        public void SquadRowsAreNormalizedInSameFrameAsDragDropTransfer()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "SquadMakerSquadRowStabilityGuard.cs"));

            Assert.That(source, Does.Contain("[DefaultExecutionOrder(32000)]"));
            Assert.That(source, Does.Contain("private void LateUpdate()"));
            Assert.That(source, Does.Contain("LayoutRebuilder.ForceRebuildLayoutImmediate(list);"));
            Assert.That(source, Does.Contain("row.localScale = Vector3.one;"));
            Assert.That(source, Does.Contain("label.horizontalAlignment = HorizontalAlignmentOptions.Left;"));
        }
    }
}
