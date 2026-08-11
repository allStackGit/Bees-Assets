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
        private Type _configDataType;
        private Type _routerType;
        private MethodInfo _shouldRedirect;
        private object _originalGameMode;
        private object _originalLevelOptions;
        private object _originalTestingLevel;
        private object _originalSeenIntro;

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _routerType = RuntimeAssembly.GetType("Assets.Scripts.Scenes.CampaignSceneRouter");
            _shouldRedirect = _routerType.GetMethod(
                "ShouldRedirectToLevelIntro",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            _originalGameMode = RuntimeAssembly.GetStaticField(_configDataType, "CurrentGameMode");
            _originalLevelOptions = RuntimeAssembly.GetStaticField(_configDataType, "LevelOptions");
            _originalTestingLevel = RuntimeAssembly.GetStaticField(_configDataType, "IsTestingLevel");
            _originalSeenIntro = RuntimeAssembly.GetStaticField(_configDataType, "HasSeenPreLevelIntro");
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "CurrentGameMode", _originalGameMode);
            RuntimeAssembly.SetStaticField(_configDataType, "LevelOptions", _originalLevelOptions);
            RuntimeAssembly.SetStaticField(_configDataType, "IsTestingLevel", _originalTestingLevel);
            RuntimeAssembly.SetStaticField(_configDataType, "HasSeenPreLevelIntro", _originalSeenIntro);
        }

        [Test]
        public void PendingCampaignBattleRedirectsSquadMakerToAuthoredIntro()
        {
            object campaignMode = Enum.Parse(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+GameModes"), "Campaign");
            object levelOptions = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.LevelOptions");

            RuntimeAssembly.SetStaticField(_configDataType, "CurrentGameMode", campaignMode);
            RuntimeAssembly.SetStaticField(_configDataType, "LevelOptions", levelOptions);
            RuntimeAssembly.SetStaticField(_configDataType, "IsTestingLevel", false);
            RuntimeAssembly.SetStaticField(_configDataType, "HasSeenPreLevelIntro", false);

            Assert.That(ShouldRedirect("Squad Maker"), Is.True);
        }

        [Test]
        public void CompletedIntroAllowsCampaignSquadSelection()
        {
            object campaignMode = Enum.Parse(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+GameModes"), "Campaign");
            object levelOptions = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.LevelOptions");

            RuntimeAssembly.SetStaticField(_configDataType, "CurrentGameMode", campaignMode);
            RuntimeAssembly.SetStaticField(_configDataType, "LevelOptions", levelOptions);
            RuntimeAssembly.SetStaticField(_configDataType, "IsTestingLevel", false);
            RuntimeAssembly.SetStaticField(_configDataType, "HasSeenPreLevelIntro", true);

            Assert.That(ShouldRedirect("Squad Maker"), Is.False);
        }

        [Test]
        public void AcceptedCampaignSquadMakerConsumesIntroPermission()
        {
            string source = ReadRouterSource();
            int seenIntroBranch = source.IndexOf("if (ConfigData.HasSeenPreLevelIntro)", StringComparison.Ordinal);
            int consume = source.IndexOf("ConfigData.HasSeenPreLevelIntro = false;", seenIntroBranch, StringComparison.Ordinal);
            int returnStatement = source.IndexOf("return;", seenIntroBranch, StringComparison.Ordinal);

            Assert.That(seenIntroBranch, Is.GreaterThanOrEqualTo(0));
            Assert.That(consume, Is.GreaterThan(seenIntroBranch));
            Assert.That(returnStatement, Is.GreaterThan(consume));
        }

        [Test]
        public void PendingCampaignIntroReplacesSquadMakerBeforeItCanRender()
        {
            string source = ReadRouterSource();
            Assert.That(source, Does.Contain("SceneManager.LoadScene(LevelIntroScene, LoadSceneMode.Single);"));
            Assert.That(source, Does.Not.Contain("SceneManager.LoadSceneAsync(LevelIntroScene, LoadSceneMode.Single);"),
                "The intro redirect must complete synchronously so Squad Maker cannot render before the intro.");
        }

        [Test]
        public void TestLevelsDoNotRedirectIntoCampaignIntro()
        {
            object campaignMode = Enum.Parse(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+GameModes"), "Campaign");
            object levelOptions = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.LevelOptions");

            RuntimeAssembly.SetStaticField(_configDataType, "CurrentGameMode", campaignMode);
            RuntimeAssembly.SetStaticField(_configDataType, "LevelOptions", levelOptions);
            RuntimeAssembly.SetStaticField(_configDataType, "IsTestingLevel", true);
            RuntimeAssembly.SetStaticField(_configDataType, "HasSeenPreLevelIntro", false);

            Assert.That(ShouldRedirect("Squad Maker"), Is.False);
        }

        private static string ReadRouterSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "CampaignSceneRouter.cs"));
        }

        private bool ShouldRedirect(string sceneName)
        {
            return (bool)_shouldRedirect.Invoke(null, new object[] { sceneName });
        }
    }
}
