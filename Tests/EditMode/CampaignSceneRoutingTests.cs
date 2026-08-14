using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignSceneRoutingTests
    {
        [Test]
        public void CampaignLoadingDoesNotUseDevelopmentStatusToSkipIntros()
        {
            string source = ReadCampaignSource();

            Assert.That(source, Does.Not.Contain("CampaignMissionCatalog.ShouldSkipPreLevelIntroForTesting(currentLevel)"));
            Assert.That(source, Does.Not.Contain("skipIntroForTesting"));
            Assert.That(source, Does.Contain("SceneManager.LoadSceneAsync(\"Level Intro\", LoadSceneMode.Single);"));
            Assert.That(source, Does.Contain("SceneManager.LoadSceneAsync(\"Squad Maker\", LoadSceneMode.Single);"));
        }

        [Test]
        public void CampaignBattleGuardClearsLegacyTestingFlag()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "CampaignPresentationGuard.cs"));

            Assert.That(source, Does.Contain("ConfigData.IsTestingLevel = false;"));
            Assert.That(source, Does.Contain("ConfigData.LevelOptions.Id < 0"));
        }

        [Test]
        public void ConfigDataCampaignRoutingLivesInDedicatedPartial()
        {
            string core = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ConfigData.cs"));
            string campaign = ReadCampaignSource();

            Assert.That(core, Does.Contain("public static partial class ConfigData"));
            Assert.That(campaign, Does.Contain("public static partial class ConfigData"));
            Assert.That(core, Does.Not.Contain("public static void LoadLevel()"));
            Assert.That(campaign, Does.Contain("public static void LoadLevel()"));
        }

        private static string ReadCampaignSource()
        {
            return File.ReadAllText(Path.Combine(
                    Application.dataPath, "Scripts", "ConfigData.Campaign.cs"))
                .Replace("\r\n", "\n");
        }
    }
}
