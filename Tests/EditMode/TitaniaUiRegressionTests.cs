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
        public void CloseButtonsAndMenuFramesAreNormalized()
        {
            string source = Read("Scripts", "UI Components", "GameHudLayoutGuard.cs");
            StringAssert.Contains("image.sprite.name.StartsWith(\"menu_button\")", source);
            StringAssert.Contains("button.gameObject.AddComponent<Outline>()", source);
            StringAssert.Contains("Mathf.Max(rect.sizeDelta.x, 28f)", source);
            StringAssert.Contains("UpdateDynamicButtonStyles();", source);
        }
    }
}
