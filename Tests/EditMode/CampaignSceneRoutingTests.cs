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
        private MethodInfo _resetNarrativeState;
        private object _originalGameMode;
        private object _originalLevelOptions;
        private object _originalTestingLevel;
        private object _originalSeenIntro;
        private object _originalSeenIntermission;

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _routerType = RuntimeAssembly.GetType("Assets.Scripts.Scenes.CampaignSceneRouter");
            _shouldRedirect = _routerType.GetMethod(
                "ShouldRedirectToLevelIntro",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            _resetNarrativeState = _routerType.GetMethod(
                "ResetNarrativeStateAtCampaignStart",
                BindingFlags.Static | BindingFlags.NonPublic);

            _originalGameMode = RuntimeAssembly.GetStaticField(_configDataType, "CurrentGameMode");
            _originalLevelOptions = RuntimeAssembly.GetStaticField(_configDataType, "LevelOptions");
            _originalTestingLevel = RuntimeAssembly.GetStaticField(_configDataType, "IsTestingLevel");
            _originalSeenIntro = RuntimeAssembly.GetStaticField(_configDataType, "HasSeenPreLevelIntro");
            _originalSeenIntermission = RuntimeAssembly.GetStaticField(_configDataType, "HasSeenIntermission");
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "CurrentGameMode", _originalGameMode);
            RuntimeAssembly.SetStaticField(_configDataType, "LevelOptions", _originalLevelOptions);
            RuntimeAssembly.SetStaticField(_configDataType, "IsTestingLevel", _originalTestingLevel);
            RuntimeAssembly.SetStaticField(_configDataType, "HasSeenPreLevelIntro", _originalSeenIntro);
            RuntimeAssembly.SetStaticField(_configDataType, "HasSeenIntermission", _originalSeenIntermission);
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
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "CampaignSceneRouter.cs"));
            int seenIntroBranch = source.IndexOf("if (ConfigData.HasSeenPreLevelIntro)", StringComparison.Ordinal);
            int consume = source.IndexOf("ConfigData.HasSeenPreLevelIntro = false;", seenIntroBranch, StringComparison.Ordinal);
            int returnStatement = source.IndexOf("return;", seenIntroBranch, StringComparison.Ordinal);

            Assert.That(seenIntroBranch, Is.GreaterThanOrEqualTo(0));
            Assert.That(consume, Is.GreaterThan(seenIntroBranch));
            Assert.That(returnStatement, Is.GreaterThan(consume));
        }

        [Test]
        public void CampaignMissionZeroClearsPriorNarrativeTransitionState()
        {
            object campaignMode = Enum.Parse(RuntimeAssembly.GetType("Assets.Scripts.ConfigData+GameModes"), "Campaign");
            object levelOptions = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.LevelOptions");
            RuntimeAssembly.SetField(levelOptions, "Id", 0);

            RuntimeAssembly.SetStaticField(_configDataType, "CurrentGameMode", campaignMode);
            RuntimeAssembly.SetStaticField(_configDataType, "LevelOptions", levelOptions);
            RuntimeAssembly.SetStaticField(_configDataType, "HasSeenPreLevelIntro", true);
            RuntimeAssembly.SetStaticField(_configDataType, "HasSeenIntermission", true);

            _resetNarrativeState.Invoke(null, new object[] { "Space" });

            Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "HasSeenPreLevelIntro"), Is.False);
            Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "HasSeenIntermission"), Is.False);
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

        private bool ShouldRedirect(string sceneName)
        {
            return (bool)_shouldRedirect.Invoke(null, new object[] { sceneName });
        }
    }
}
