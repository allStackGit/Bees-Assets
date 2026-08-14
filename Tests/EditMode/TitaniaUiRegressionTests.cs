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
        public void TitaniaOnePursuitReinforcementsAreIncreasedByHalf()
        {
            string source = Read("Scripts", "Levels", "Titania1Minesweeper.cs");
            StringAssert.Contains("ConfigData.ShipTypes.Hornet, 9, true, true", source);
            StringAssert.Contains("ConfigData.ShipTypes.Wasp, 6, true, true", source);
            StringAssert.Contains("ConfigData.ShipTypes.Leafcutter, 3, true, true", source);
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
        public void MenuFramesUseSlicedSpritesButCloseXReturnsToAuthoredSize()
        {
            string frameSource = Read("Scripts", "UI Components", "GameHudLayoutGuard.cs");
            string sizingSource = Read("Scripts", "UI Components", "UiSizingCompatibilityGuard.cs");
            string menuButtonMeta = Read("Sprites", "UI", "menu_button.png.meta");
            string alternateButtonMeta = Read("Sprites", "UI", "menu_button_alt.png.meta");
            string resetButtonMeta = Read("Sprites", "UI", "menu_button_reset.png.meta");

            StringAssert.Contains("image.sprite.name.StartsWith(\"menu_button\")", frameSource);
            StringAssert.Contains("image.type = Image.Type.Sliced;", frameSource);
            StringAssert.DoesNotContain("AddComponent<Outline>()", frameSource);
            StringAssert.Contains("border: {x: 4, y: 4, z: 4, w: 4}", menuButtonMeta);
            StringAssert.Contains("border: {x: 4, y: 4, z: 4, w: 4}", alternateButtonMeta);
            StringAssert.Contains("border: {x: 4, y: 4, z: 4, w: 4}", resetButtonMeta);
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
        public void TitaniaRoutePersistsInPlayerProgressAndIsAppliedToBeenoculars()
        {
            string route = Read("Scripts", "Levels", "TitaniaRouteState.cs");
            string userData = Read("Scripts", "Data", "UserData.cs");
            string checkpoint = Read("Scripts", "CampaignCheckpoint.cs");
            string minesweeper = Read("Scripts", "Levels", "Titania1Minesweeper.cs");

            StringAssert.Contains("ProgressProperty = \"TitaniaOpenedBarrierPositions\"", route);
            StringAssert.Contains("progress[ProgressProperty] = new JArray", route);
            StringAssert.Contains("LoadFromPlayerProgress", userData);
            StringAssert.Contains("AddToPlayerProgressJson", userData);
            StringAssert.Contains("TitaniaRouteState.AddToPlayerProgressJson", checkpoint);
            StringAssert.DoesNotContain("PlayerPrefs.SetString", route);
            StringAssert.Contains("ConfigData.LevelOptions.Obstacles = \"Minesweeper\";", route);
            StringAssert.Contains("CanisterBomb[] demolitionObjects", route);
            StringAssert.Contains("demolitionObject.TargetObstacle = barrier;", route);
            StringAssert.Contains("TitaniaRouteState.WasBarrierOpened", route);
            StringAssert.Contains("barrier.Kill();", route);
            StringAssert.Contains("demolitionObject.gameObject.SetActive(true);", route);
            StringAssert.Contains("BeginTitania1DemolitionTracking();", minesweeper);
            StringAssert.Contains("TitaniaRouteState.RecordOpenedBarrier", minesweeper);
        }

        [Test]
        public void TitaniaTwoAddsMirroredIntervalWavesAndPlutoStyleBaseHealth()
        {
            string source = Read("Scripts", "Levels", "Level.Titania2Enhancements.cs");
            StringAssert.Contains("Stage.Menus.PlutoShield.SetActive(true);", source);
            StringAssert.Contains("UpdateTitania2BaseHealth(titania, Stage.Menus.PlutoShieldHealthBar);", source);
            StringAssert.Contains("healthBar.transform.localScale = new Vector2(fraction * 150f, 1f);", source);
            StringAssert.DoesNotContain("baseHealthLabel.text = \"Base Health\";", source);
            StringAssert.Contains("ScheduleTitania2ExtraWave(25f, -0.65f, -1f", source);
            StringAssert.Contains("ScheduleTitania2ExtraWave(315f, -0.55f, -1f", source);
            StringAssert.Contains("AddTitania2BeeWave(squads, normalizedX, normalizedY);", source);
        }
    }
}
