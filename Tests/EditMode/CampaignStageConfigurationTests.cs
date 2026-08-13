using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignStageConfigurationTests
    {
        [Test]
        public void CampaignTriggersUsePersistedProgressMissionIdentity()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));

            Assert.That(source, Does.Contain("UserProgressData.GetCurrentLevel("));
            Assert.That(source, Does.Contain("CampaignMissionCatalog.Configure(this, missionId);"));
            Assert.That(source, Does.Not.Contain("CampaignMissionCatalog.Configure(this, CurrentLevelOptions.Id);"));
        }

        [Test]
        public void AffectedCampaignMissionsInstallTheirCompatibilitySetup()
        {
            string shared = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));
            string rules = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "CampaignObjectiveRules.cs"));

            Assert.That(shared, Does.Contain("if (missionId == 4)"));
            Assert.That(shared, Does.Contain("Neptune1SeizeTheMeansWithEndingContinuation();"));
            Assert.That(shared, Does.Contain("else if (missionId == 9)"));
            Assert.That(shared, Does.Contain("Uranus1OnTheOffensiveWithAuthoredFog();"));

            Assert.That(rules, Does.Contain("ContinuationName = \"Level 4 Post-success dialogue\""));
            Assert.That(rules, Does.Contain("Level.NextTriggers.Remove(continuation);"));
            Assert.That(rules, Does.Contain("CurrentLevelOptions.FogOfWar != 1"));
            Assert.That(rules, Does.Contain("Map.FogOfWar.SetActive(true);"));
            Assert.That(rules, Does.Contain("ship.FogOfWarVision.Activate();"));
        }

        [Test]
        public void CampaignStageDisablesGenericMapAndSquadOverrides()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "CampaignStageConfiguration.cs"));

            Assert.That(source, Does.Contain("stage.HasRandomizedOptions = true;"));
            Assert.That(source, Does.Contain("stage.OverrideMapIndex = mission.MapIndex;"));
            Assert.That(source, Does.Contain("stage.GeneratedSquadCountOverride = 0;"));
            Assert.That(source, Does.Contain("stage.UseFullyRandomSquads = false;"));
            Assert.That(source, Does.Contain("stage.UseFullyRandomEnemySquads = false;"));
        }

        [Test]
        public void CampaignTestLevelsKeepTheirAdHocLevelOptions()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "CampaignStageConfiguration.cs"));

            Assert.That(source, Does.Contain("if (ConfigData.IsTestingLevel ||"));
            Assert.That(source, Does.Contain("ConfigData.LevelOptions != null && ConfigData.LevelOptions.Id < 0"));
            Assert.That(source.IndexOf("if (ConfigData.IsTestingLevel ||", StringComparison.Ordinal),
                Is.LessThan(source.IndexOf("int missionId = ConfigData.UserProgressData.GetCurrentLevel(", StringComparison.Ordinal)),
                "Test-mode bypass must happen before persisted campaign mission configuration is applied.");
        }
    }
}
