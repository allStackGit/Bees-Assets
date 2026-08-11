using NUnit.Framework;
using System;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ConfigurationSettingsTests
    {
        private Type _configDataType;
        private object _previousConfiguration;
        private object _previousShipTurningRadius;

        private const string Contents = @"{
            ""IsDeadVersion"": false,
            ""UseLocalStorage"": false,
            ""MirrorStorage"": false,
            ""CensoredWords"": [],
            ""MaxSquadSize"": 10,
            ""MaxSquadWidth"": -1,
            ""MaxSquadHeight"": 10,
            ""RotationMultiplier"": 16,
            ""HumanSide"": 2,
            ""BeeSide"": 1,
            ""DoesUserHaveController"": false,
            ""StorageChunkSize"": 2500000,
            ""StandardMaxTimeOnQueue"": 25,
            ""TimeScale"": 1,
            ""AISquadPatrolTime"": 60,
            ""AIPatrolMaxSize"": 100,
            ""AIRandomMovementMaxDistance"": 200,
            ""AISquadGuardTime"": -1,
            ""AISquadFollowingTime"": -1,
            ""CarrierCarryDroneMax"": 4,
            ""CarrierCarryStrikerMax"": 4,
            ""CarrierSquadCount"": 2,
            ""TotalLevels"": 10,
            ""Tooltips"": [],
            ""Yes"": ""Yes"",
            ""No"": ""No"",
            ""OK"": ""OK"",
            ""AreYouSure"": ""Sure?"",
            ""AreYouSureExit"": ""Exit?"",
            ""LevelProgressLost"": ""Lost"",
            ""DeleteSquadConfirmation"": ""Delete"",
            ""ClearSquadConfirmation"": ""Clear"",
            ""LoadSquadConfirmation"": ""Load"",
            ""ChooseSquadConfirmation"": ""Choose"",
            ""UnchooseSquadConfirmation"": ""Unchoose"",
            ""GoBackConfirmation"": ""Back"",
            ""OverCapacityAlertTitle"": ""Capacity"",
            ""NoChosenSquadsAlertTitle"": ""None"",
            ""ChoosingUnsavedSquadAlertTitle"": ""Unsaved"",
            ""ChoosingDeadSquadAlertTitle"": ""Dead"",
            ""SquadSavingStatusAlertTitle"": ""Saving"",
            ""OverCapacityAlert"": ""Capacity body"",
            ""NoChosenSquadsAlert"": ""None body"",
            ""ChoosingUnsavedSquadAlert"": ""Unsaved body"",
            ""ChoosingDeadSquadAlert"": ""Dead body"",
            ""SquadSavingStatusAlert"": ""Saving body"",
            ""AISide"": ""BeeSide"",
            ""UserSide"": ""HumanSide"",
            ""SquadMakerFirstSide"": ""HumanSide"",
            ""SquadMakerSecondSide"": ""BeeSide""
        }";

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _previousConfiguration = RuntimeAssembly.GetStaticField(_configDataType, "Configuration");
            _previousShipTurningRadius = RuntimeAssembly.GetStaticField(_configDataType, "ShipTurningRadius");
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", _previousConfiguration);
            RuntimeAssembly.SetStaticField(_configDataType, "ShipTurningRadius", _previousShipTurningRadius);
        }

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
        public void ProcessDataUsesCatalogMissionCountWhenServerTotalIsStale()
        {
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");

            RuntimeAssembly.Invoke(configuration, "ProcessData", Contents);

            Assert.That(
                RuntimeAssembly.GetField(configuration, "TotalLevels"),
                Is.EqualTo(12));
        }

        [Test]
        public void LoadedConfigurationOwnsDefaultRequestTimeout()
        {
            var configurationType = RuntimeAssembly.GetType("Assets.Scripts.Settings.Configuration");
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");

            RuntimeAssembly.Invoke(configuration, "ProcessData", Contents);
            RuntimeAssembly.SetField(configuration, "IsLoaded", true);
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", configuration);

            Assert.That(
                RuntimeAssembly.InvokeStatic(configurationType, "GetStandardMaxTimeOnQueue"),
                Is.EqualTo(25));
        }

        [Test]
        public void UnloadedConfigurationKeepsBootstrapRequestTimeout()
        {
            var configurationType = RuntimeAssembly.GetType("Assets.Scripts.Settings.Configuration");
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");

            RuntimeAssembly.Invoke(configuration, "ProcessData", Contents);
            RuntimeAssembly.SetField(configuration, "IsLoaded", false);
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", configuration);

            Assert.That(
                RuntimeAssembly.InvokeStatic(configurationType, "GetStandardMaxTimeOnQueue"),
                Is.EqualTo(10));
        }
    }
}