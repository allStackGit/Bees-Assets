using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignSceneRoutingTests
    {
        private Type _catalogType;
        private MethodInfo _shouldSkipIntro;

        [SetUp]
        public void SetUp()
        {
            _catalogType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignMissionCatalog");
            _shouldSkipIntro = _catalogType.GetMethod(
                "ShouldSkipPreLevelIntroForTesting",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void ReadyCampaignMissionsKeepAuthoredIntroFlow(int missionId)
        {
            Assert.That(ShouldSkipIntro(missionId), Is.False);
        }

        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        [TestCase(10)]
        [TestCase(11)]
        public void InDevelopmentCampaignMissionsBypassPreLevelIntro(int missionId)
        {
            Assert.That(ShouldSkipIntro(missionId), Is.True);
        }

        [Test]
        public void CampaignLoadingUsesTestingStatusInsteadOfUnconditionalReturn()
        {
            string source = ReadCampaignSource();

            Assert.That(source, Does.Contain("CampaignMissionCatalog.ShouldSkipPreLevelIntroForTesting(currentLevel)"));
            Assert.That(source, Does.Contain("bool skipIntroForTesting = IsTestingLevel ||"));
            Assert.That(source, Does.Contain("else if (!HasSeenPreLevelIntro)"));
            Assert.That(source, Does.Contain("SceneManager.LoadSceneAsync(\"Level Intro\", LoadSceneMode.Single);"));
            Assert.That(source, Does.Contain("SceneManager.LoadSceneAsync(\"Squad Maker\", LoadSceneMode.Single);"));
            Assert.That(source, Does.Not.Contain("SceneManager.LoadSceneAsync(\"Squad Maker\", LoadSceneMode.Single);\n                    return;"),
                "Campaign routing must not unconditionally bypass the intro for every later mission.");
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

        private bool ShouldSkipIntro(int missionId)
        {
            return (bool)_shouldSkipIntro.Invoke(null, new object[] { missionId });
        }

        private static string ReadCampaignSource()
        {
            return File.ReadAllText(Path.Combine(
                    Application.dataPath, "Scripts", "ConfigData.Campaign.cs"))
                .Replace("\r\n", "\n");
        }
    }
}
