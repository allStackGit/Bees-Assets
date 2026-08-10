using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ConfigurationSettingsTests
    {
        private const string Contents = @"{
            \"IsDeadVersion\": false,
            \"UseLocalStorage\": false,
            \"MirrorStorage\": false,
            \"CensoredWords\": [],
            \"MaxSquadSize\": 10,
            \"MaxSquadWidth\": -1,
            \"MaxSquadHeight\": 10,
            \"RotationMultiplier\": 16,
            \"HumanSide\": 2,
            \"BeeSide\": 1,
            \"DoesUserHaveController\": false,
            \"StorageChunkSize\": 2500000,
            \"StandardMaxTimeOnQueue\": 25,
            \"TimeScale\": 1,
            \"AISquadPatrolTime\": 60,
            \"AIPatrolMaxSize\": 100,
            \"AIRandomMovementMaxDistance\": 200,
            \"AISquadGuardTime\": -1,
            \"AISquadFollowingTime\": -1,
            \"CarrierCarryDroneMax\": 4,
            \"CarrierCarryStrikerMax\": 4,
            \"CarrierSquadCount\": 2,
            \"TotalLevels\": 32,
            \"Tooltips\": [],
            \"Yes\": \"Yes\",
            \"No\": \"No\",
            \"OK\": \"OK\",
            \"AreYouSure\": \"Sure?\",
            \"AreYouSureExit\": \"Exit?\",
            \"LevelProgressLost\": \"Lost\",
            \"DeleteSquadConfirmation\": \"Delete\",
            \"ClearSquadConfirmation\": \"Clear\",
            \"LoadSquadConfirmation\": \"Load\",
            \"ChooseSquadConfirmation\": \"Choose\",
            \"UnchooseSquadConfirmation\": \"Unchoose\",
            \"GoBackConfirmation\": \"Back\",
            \"OverCapacityAlertTitle\": \"Capacity\",
            \"NoChosenSquadsAlertTitle\": \"None\",
            \"ChoosingUnsavedSquadAlertTitle\": \"Unsaved\",
            \"ChoosingDeadSquadAlertTitle\": \"Dead\",
            \"SquadSavingStatusAlertTitle\": \"Saving\",
            \"OverCapacityAlert\": \"Capacity body\",
            \"NoChosenSquadsAlert\": \"None body\",
            \"ChoosingUnsavedSquadAlert\": \"Unsaved body\",
            \"ChoosingDeadSquadAlert\": \"Dead body\",
            \"SquadSavingStatusAlert\": \"Saving body\",
            \"AISide\": \"BeeSide\",
            \"UserSide\": \"HumanSide\",
            \"SquadMakerFirstSide\": \"HumanSide\",
            \"SquadMakerSecondSide\": \"BeeSide\"
        }";

        [Test]
        public void ProcessDataLoadsConfiguredAiRandomMovementDistance()
        {
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");

            RuntimeAssembly.Invoke(configuration, "ProcessData", Contents);

            Assert.That(
                RuntimeAssembly.GetField(configuration, "AIRandomMovementMaxDistance"),
                Is.EqualTo(200));
        }

        [Test]
        public void LoadedConfigurationOwnsDefaultRequestTimeout()
        {
            var configurationType = RuntimeAssembly.GetType("Assets.Scripts.Settings.Configuration");
            var configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object previousConfiguration = RuntimeAssembly.GetStaticField(configDataType, "Configuration");
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");

            try
            {
                RuntimeAssembly.Invoke(configuration, "ProcessData", Contents);
                RuntimeAssembly.SetField(configuration, "IsLoaded", true);
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", configuration);

                Assert.That(
                    RuntimeAssembly.InvokeStatic(configurationType, "GetStandardMaxTimeOnQueue"),
                    Is.EqualTo(25));
            }
            finally
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", previousConfiguration);
            }
        }

        [Test]
        public void UnloadedConfigurationKeepsBootstrapRequestTimeout()
        {
            var configurationType = RuntimeAssembly.GetType("Assets.Scripts.Settings.Configuration");
            var configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object previousConfiguration = RuntimeAssembly.GetStaticField(configDataType, "Configuration");
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");

            try
            {
                RuntimeAssembly.Invoke(configuration, "ProcessData", Contents);
                RuntimeAssembly.SetField(configuration, "IsLoaded", false);
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", configuration);

                Assert.That(
                    RuntimeAssembly.InvokeStatic(configurationType, "GetStandardMaxTimeOnQueue"),
                    Is.EqualTo(10));
            }
            finally
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", previousConfiguration);
            }
        }
    }
}
