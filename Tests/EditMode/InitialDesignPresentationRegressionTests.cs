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
        public void ScriptedCampaignCameraLetsOverrideScoutExitMapButClampsOtherTargets()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "CampaignCameraFollowGuard.cs"));

            Assert.That(source, Does.Contain("_stage.IsFollowingShip"));
            Assert.That(source, Does.Contain("_stage.IsCameraMovingToTarget"));
            Assert.That(source, Does.Contain("if (!ShouldAllowFollowOutsideMap(cameraShip))"),
                "The scripted Pluto I Scout must remain camera-followable while it exits the playable area.");
            Assert.That(source, Does.Contain("cameraShip.CanOverrideBounds"));
            Assert.That(source, Does.Contain("cameraShip.ShipType == ConfigData.ShipTypes.Scout"));
            Assert.That(source, Does.Contain("ClampCameraToMap(_stage.Camera);"));
            Assert.That(source, Does.Contain("mapBounds.extents.y"));
            Assert.That(source, Does.Contain("mapBounds.extents.x / camera.aspect"));
            Assert.That(source, Does.Contain("camera.orthographicSize = maximumVerticalSize;"));
        }

        [Test]
        public void PlutoOnePreservesSelectedGameSpeedAcrossScoutDialogueHandoff()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "CampaignPresentationGuard.cs"));

            Assert.That(source, Does.Contain("cameraShip.ShipType == ConfigData.ShipTypes.Scout"));
            Assert.That(source, Does.Contain("_plutoOneTimeScale = Mathf.Clamp(stage.TimeScale, 1f, 2f);"));
            Assert.That(source, Does.Contain("cameraShip.ShipType == ConfigData.ShipTypes.Gunship"));
            Assert.That(source, Does.Contain("stage.TimeScale = _plutoOneTimeScale;"));
            Assert.That(source, Does.Contain("Time.timeScale = stage.TimeScale;"));
            Assert.That(source, Does.Contain("stage.Menus.GameSpeedButtonText.text = $\"{stage.TimeScale}x\";"));
        }

        [Test]
        public void TooltipIsLaidOutBeforeItIsRendered()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "Tooltip.cs"));

            Assert.That(source, Does.Contain("private Vector2 _requestedSize = Vector2.zero;"));
            Assert.That(source, Does.Contain("TooltipObject.SetActive(false);"));
            Assert.That(source, Does.Contain("ApplyLayout();"));
            Assert.That(source, Does.Contain("Canvas.ForceUpdateCanvases();"));
            Assert.That(source, Does.Contain("TooltipObject.SetActive(true);"));
        }

        [Test]
        public void PlutoOneAttackOnSightClosesItsTutorialPrompt()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Pluto.cs"));

            Assert.That(source, Does.Contain("firstGunship.Squad.AttackOnSight"));
            Assert.That(source, Does.Contain("attackOnSightTooltip.Hide();"));
            Assert.That(source, Does.Contain("Level 0 Closing Attack on Sight prompt"));
        }

        [Test]
        public void PlutoTwoTutorialOwnsSizingAndDialogueOrderBeforeRendering()
        {
            string missionSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Pluto.cs"));
            string guardSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "PlutoTwoTutorialPresentationGuard.cs"));
            string campaignGuardSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "CampaignFeedbackAdjustmentGuard.cs"));

            Assert.That(missionSource, Does.Contain("the number hotkeys on your keyboard"));
            Assert.That(campaignGuardSource, Does.Contain("bool squadNumberPage = Contains(text, \"number hotkeys\");"));
            Assert.That(campaignGuardSource, Does.Contain("EnsureSquadNumberArrow();"));
            Assert.That(missionSource, Does.Contain("basicTooltip.Place(new Vector2(0, -160), new Vector2(150, 100));"));
            Assert.That(missionSource, Does.Contain("squadNumberHighlightRect.localScale = Vector3.one;"));
            Assert.That(missionSource, Does.Contain("squadNumberHighlightRect.sizeDelta = new Vector2(150f, 30f);"));
            Assert.That(missionSource, Does.Not.Contain("squadNumberHighlight.transform.localScale = new Vector2(150, 30);"));

            int sequence = missionSource.IndexOf("basicTooltip.ShowSequence(new List<string>");
            int combatTrigger = missionSource.IndexOf("Level 1 start combat", sequence);
            Assert.That(sequence, Is.GreaterThanOrEqualTo(0));
            Assert.That(combatTrigger, Is.GreaterThan(sequence));
            string sequenceBlock = missionSource.Substring(sequence, combatTrigger - sequence);
            Assert.That(sequenceBlock, Does.Contain("tacticalTutorialComplete = true;"));
            Assert.That(sequenceBlock, Does.Contain("PlayDialogueSection(Stage.CutsceneManager.PlutoLines_Reinforcements.GetRange(3, 2))"));
            Assert.That(sequenceBlock, Does.Contain("Stage.Menus.TogglePausePanel();"));

            Assert.That(guardSource, Does.Contain("CenterMissionStatus(_stage, _statusCorners, _canvasCorners);"));
            Assert.That(guardSource, Does.Not.Contain("dialogueManager.enabled"));
            Assert.That(guardSource, Does.Not.Contain("HoldDialogueUntilTutorialEnds"));
            Assert.That(guardSource, Does.Not.Contain("RepairOverscaledTutorialHighlight"));
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
