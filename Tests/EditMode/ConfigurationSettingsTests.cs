using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ConfigurationSettingsTests
    {
        [Test]
        public void ProcessDataLoadsConfiguredAiRandomMovementDistance()
        {
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");

            const string contents = @"{
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

            RuntimeAssembly.Invoke(configuration, "ProcessData", contents);

            Assert.That(
                RuntimeAssembly.GetField(configuration, "AIRandomMovementMaxDistance"),
                Is.EqualTo(200));
        }
    }
}
