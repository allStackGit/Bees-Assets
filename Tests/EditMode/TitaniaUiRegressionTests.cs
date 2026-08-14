using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TitaniaUiRegressionTests
    {
        private static string Read(params string[] parts)
        {
            string path = Application.dataPath;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path);
        }

        [Test]
        public void SquadMakerSceneLoadedCallbackUsesUnitySceneType()
        {
            string source = Read("Scripts", "Scenes", "SquadMakerMapDropdownGuard.cs");
            StringAssert.Contains(
                "HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)",
                source);
        }

        [Test]
        public void MiningCommandsChooseStableNearCenterOffsets()
        {
            string source = Read("Scripts", "Levels", "Commands", "Mining.cs");
            StringAssert.Contains("_miningDestinationOffset = UnityEngine.Random.insideUnitCircle * spreadRadius;", source);
            StringAssert.Contains("TargetAstroid.GetPosition() + _miningDestinationOffset", source);
            StringAssert.Contains("_miningDestinationOffset = Vector2.zero;", source);
        }

        [Test]
        public void TitaniaDebugObstacleRenderersAreMadeTransparent()
        {
            string source = Read("Scripts", "Levels", "Level.Environment.cs");
            StringAssert.Contains("obstacleName != \"Minesweeper\" && obstacleName != \"Bee-noculars\"", source);
            StringAssert.Contains("SpriteRenderer debugBackground = obstacle.GetComponent<SpriteRenderer>();", source);
            StringAssert.Contains("color.a = 0f;", source);
        }

        [Test]
        public void TitaniaOneProvidesEncounterOnlySingletonPatrolFallbacks()
        {
            string source = Read("Scripts", "Levels", "CampaignObjectiveRules.cs");
            StringAssert.Contains("AddTemporaryTitaniaPatrols(_titania1TemporaryPatrolSquads, ConfigData.ShipTypes.Hornet, 8);", source);
            StringAssert.Contains("AddTemporaryTitaniaPatrols(_titania1TemporaryPatrolSquads, ConfigData.ShipTypes.Wasp, 7);", source);
            StringAssert.Contains("AddTemporaryTitaniaPatrols(_titania1TemporaryPatrolSquads, ConfigData.ShipTypes.Leafcutter, 2);", source);
            StringAssert.Contains("ConfigData.CurrentShips.RemoveSquad(patrol);", source);
            StringAssert.Contains("new SquadShip(fleetShip, Vector2.zero)", source);
        }

        [Test]
        public void CarrierProgressionMovesFromNeptuneThreeToTitaniaTwo()
        {
            string source = Read("Scripts", "Levels", "CampaignObjectiveRules.cs");
            StringAssert.Contains("Neptune3EndingWithoutCarrier", source);
            StringAssert.Contains("Titania2CampaignEndingWithCarrierUnlock", source);
            StringAssert.Contains("missionId == 6 && ending.Method.Name == nameof(Level.Neptune3Ending)", source);
            StringAssert.Contains("missionId == 8 && ending.Method.Name == nameof(Level.Titania2CampaignEnding)", source);
            StringAssert.Contains("UnlockedCampaignShips.Contains(ConfigData.ShipTypes.Carrier)", source);
        }

        [Test]
        public void CampaignEscapeZonesReceiveMinimapMarkers()
        {
            string source = Read("Scripts", "Levels", "CampaignObjectiveRules.cs");
            StringAssert.Contains("GetComponentsInChildren<Zone>(true)", source);
            StringAssert.Contains("level.Stage.Prefabs.MinimapCircle", source);
            StringAssert.Contains("Exit Zone Minimap Marker", source);
        }

        [Test]
        public void MenuFramesRemainNormalizedButCloseXReturnsToAuthoredSize()
        {
            string frameSource = Read("Scripts", "UI Components", "GameHudLayoutGuard.cs");
            string sizingSource = Read("Scripts", "UI Components", "UiSizingCompatibilityGuard.cs");
            StringAssert.Contains("image.sprite.name.StartsWith(\"menu_button\")", frameSource);
            StringAssert.Contains("button.gameObject.AddComponent<Outline>()", frameSource);
            StringAssert.Contains("rect.sizeDelta = new Vector2(16f, 16f);", sizingSource);
            StringAssert.Contains("rect.sizeDelta.x - 4f", sizingSource);
        }

        [Test]
        public void TooltipAndMissionSummaryCloseOnPointerDown()
        {
            string tooltip = Read("Scripts", "UI Components", "Tooltip.cs");
            string summary = Read("Scripts", "UI Components", "SummaryClosePressGuard.cs");
            StringAssert.Contains("EventTriggerType.PointerDown", tooltip);
            StringAssert.Contains("press.callback.AddListener(_ => Hide());", tooltip);
            StringAssert.Contains("EventTriggerType.PointerDown", summary);
            StringAssert.Contains("menus.HideMissionSummary();", summary);
        }

        [Test]
        public void SquadBoxesUseDeterministicSortingOrder()
        {
            string source = Read("Scripts", "Levels", "Squad.UI.cs");
            StringAssert.Contains("boxRenderer.sortingOrder = SquadNumber > 0 ? SquadNumber : ItemId;", source);
        }

        [Test]
        public void CampaignPresentationDoesNotUseMissionDevelopmentStatusToSkipIntros()
        {
            string routing = Read("Scripts", "ConfigData.Campaign.cs");
            string guard = Read("Scripts", "Scenes", "CampaignPresentationGuard.cs");
            StringAssert.DoesNotContain("ShouldSkipPreLevelIntroForTesting(currentLevel)", routing);
            StringAssert.Contains("ConfigData.IsTestingLevel = false;", guard);
        }

        [Test]
        public void TitaniaRoutePersistsAndIsAppliedToBeenoculars()
        {
            string route = Read("Scripts", "Levels", "TitaniaRouteState.cs");
            string minesweeper = Read("Scripts", "Levels", "Titania1Minesweeper.cs");
            StringAssert.Contains("PlayerPrefs.SetString", route);
            StringAssert.Contains("ConfigData.LevelOptions.Obstacles = \"Minesweeper\";", route);
            StringAssert.Contains("TitaniaRouteState.WasBarrierOpened", route);
            StringAssert.Contains("nearestBarrier.Kill();", route);
            StringAssert.Contains("BeginTitania1DemolitionTracking();", minesweeper);
            StringAssert.Contains("TitaniaRouteState.RecordOpenedBarrier", minesweeper);
        }

        [Test]
        public void TitaniaTwoAddsMirroredIntervalWavesAndBaseHealth()
        {
            string source = Read("Scripts", "Levels", "Level.Titania2Enhancements.cs");
            StringAssert.Contains("baseHealthLabel.text = \"Base Health\";", source);
            StringAssert.Contains("ScheduleTitania2ExtraWave(25f, -0.65f, -1f", source);
            StringAssert.Contains("ScheduleTitania2ExtraWave(315f, -0.55f, -1f", source);
            StringAssert.Contains("AddTitania2BeeWave(squads, normalizedX, normalizedY);", source);
        }
    }
}
