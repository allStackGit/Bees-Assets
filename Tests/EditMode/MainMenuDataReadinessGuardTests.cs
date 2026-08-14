using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MainMenuDataReadinessGuardTests
    {
        [Test]
        public void CampaignButtonIsBlockedUntilUserDataAndMenuFinalizationAreReady()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenuDataReadinessGuard.cs"));

            Assert.That(source, Does.Contain("SceneManager.sceneLoaded += HandleSceneLoaded;"));
            Assert.That(source, Does.Contain("_campaignButton.enabled = false;"));
            Assert.That(source, Does.Contain("!ConfigData.AreAllSettingsLoaded"));
            Assert.That(source, Does.Contain("!ConfigData.IsAllUserDataLoaded"));
            Assert.That(source, Does.Contain("ConfigData.UserProgressData == null"));
            Assert.That(source, Does.Contain("ConfigData.CampaignShips == null"));
            Assert.That(source, Does.Contain("ConfigData.GetCampaignLevelData() == null"));
            Assert.That(source, Does.Contain("!_mainMenu.IsFinalized"));
        }

        [Test]
        public void CampaignButtonRemainsDisabledWhenCampaignIsComplete()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenuDataReadinessGuard.cs"));

            Assert.That(source, Does.Contain("CampaignMissionCatalog.IsCampaignComplete(currentLevel)"));
            Assert.That(source, Does.Contain("!_mainMenu.IsResettingCampaign"));
        }

        [Test]
        public void CampaignResetBuildsFreshRemoteDefaultsBeforeReenablingPlay()
        {
            string mainMenu = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenu.cs"));
            string runtime = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "ConfigData.Runtime.cs"));
            string userData = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "UserData.cs"));

            Assert.That(mainMenu, Does.Contain("SetupCampaignFleetData(false, allCampaignStartingShips, forceCreateDefaults: true)"));
            Assert.That(mainMenu, Does.Contain("SetupCampaignSavedSquadsData(false, forceCreateDefaults: true)"));
            Assert.That(mainMenu, Does.Contain("TitaniaRouteState.ResetForCampaignRestart();"));
            Assert.That(mainMenu, Does.Contain("HumanCampaignModeButton.GetComponent<Button>().enabled = false;"));
            Assert.That(mainMenu, Does.Contain("HumanCampaignModeButton.GetComponent<Button>().enabled = true;"));
            Assert.That(runtime, Does.Contain("bool forceCreateDefaults = false"));
            Assert.That(userData, Does.Contain("!ConfigData.Configuration.UseLocalStorage && !forceCreateDefaults"));
        }
    }
}
