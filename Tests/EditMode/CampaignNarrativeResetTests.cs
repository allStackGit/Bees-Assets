using System;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignNarrativeResetTests
    {
        [Test]
        public void CampaignMissionZeroClearsPriorNarrativeTransitionState()
        {
            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            Type routerType = RuntimeAssembly.GetType("Assets.Scripts.Scenes.CampaignSceneRouter");
            MethodInfo resetNarrativeState = routerType.GetMethod(
                "ResetNarrativeStateAtCampaignStart",
                BindingFlags.Static | BindingFlags.NonPublic);

            object originalGameMode = RuntimeAssembly.GetStaticField(configDataType, "CurrentGameMode");
            object originalLevelOptions = RuntimeAssembly.GetStaticField(configDataType, "LevelOptions");
            object originalSeenIntro = RuntimeAssembly.GetStaticField(configDataType, "HasSeenPreLevelIntro");
            object originalSeenIntermission = RuntimeAssembly.GetStaticField(configDataType, "HasSeenIntermission");

            try
            {
                object campaignMode = Enum.Parse(
                    RuntimeAssembly.GetType("Assets.Scripts.ConfigData+GameModes"),
                    "Campaign");
                object levelOptions = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.LevelOptions");
                RuntimeAssembly.SetField(levelOptions, "Id", 0);

                RuntimeAssembly.SetStaticField(configDataType, "CurrentGameMode", campaignMode);
                RuntimeAssembly.SetStaticField(configDataType, "LevelOptions", levelOptions);
                RuntimeAssembly.SetStaticField(configDataType, "HasSeenPreLevelIntro", true);
                RuntimeAssembly.SetStaticField(configDataType, "HasSeenIntermission", true);

                resetNarrativeState.Invoke(null, new object[] { "Space" });

                Assert.That(RuntimeAssembly.GetStaticField(configDataType, "HasSeenPreLevelIntro"), Is.False);
                Assert.That(RuntimeAssembly.GetStaticField(configDataType, "HasSeenIntermission"), Is.False);
            }
            finally
            {
                RuntimeAssembly.SetStaticField(configDataType, "CurrentGameMode", originalGameMode);
                RuntimeAssembly.SetStaticField(configDataType, "LevelOptions", originalLevelOptions);
                RuntimeAssembly.SetStaticField(configDataType, "HasSeenPreLevelIntro", originalSeenIntro);
                RuntimeAssembly.SetStaticField(configDataType, "HasSeenIntermission", originalSeenIntermission);
            }
        }
    }
}
