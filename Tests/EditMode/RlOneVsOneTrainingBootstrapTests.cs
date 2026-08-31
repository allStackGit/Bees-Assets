using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneTrainingBootstrapTests
    {
        private GameObject _stageObject;
        private Component _stage;
        private Type _bootstrapType;

        [SetUp]
        public void SetUp()
        {
            _stageObject = new GameObject(nameof(RlOneVsOneTrainingBootstrapTests));
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            ((Behaviour)_stage).enabled = false;
            _bootstrapType = RuntimeAssembly.GetType("RlOneVsOneTrainingBootstrap");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_stageObject);
        }

        [Test]
        public void DedicatedSceneAppliesMinimalOneVsOneTrainingConfiguration()
        {
            // The dedicated scene was copied from a player-facing stage and may retain these flags.
            // Training skips AudioController.Setup(), so the bootstrap must normalize them explicitly.
            RuntimeAssembly.SetField(_stage, "ActivateAudio", true);
            RuntimeAssembly.SetField(_stage, "PlayMusic", true);

            ApplyBootstrap();

            Assert.That(RuntimeAssembly.GetField(_stage, "IsTrainingHiveMind"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "IsTrainingNueralNetwork"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_stage, "ActivateHiveMind"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "ActivateBrains"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "DoesUserHaveController"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "UseFullyRandomSquads"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_stage, "HasRandomizedOptions"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "IsRendering"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_stage, "ActivateAudio"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "PlayMusic"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "LevelCount"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(_stage, "GeneratedSquadCountOverride"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(_stage, "OverrideMapIndex"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetField(_stage, "TimeoutTime"), Is.EqualTo(120));
        }

        [Test]
        public void FirstProofUsesRequestedMapAndMatchup()
        {
            Assert.That(GetBootstrapConstant("TrainingMapSize"), Is.EqualTo(120f));
            Assert.That(GetBootstrapConstant("SpawnRadius"), Is.EqualTo(30f));
            Assert.That(GetBootstrapConstant("BeeShipType").ToString(), Is.EqualTo("Wasp"));
            Assert.That(GetBootstrapConstant("HumanShipType").ToString(), Is.EqualTo("Gunship"));
        }

        [Test]
        public void BootstrapOnlyBindsTheDedicatedRlScene()
        {
            MethodInfo shouldApply = _bootstrapType.GetMethod(
                "ShouldApply",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(shouldApply, Is.Not.Null);

            Assert.That(shouldApply.Invoke(null, new object[] { "RL 1v1 Training" }), Is.True);
            Assert.That(shouldApply.Invoke(null, new object[] { "Space" }), Is.False);
            Assert.That(shouldApply.Invoke(null, new object[] { "Hivemind Training Downsized" }), Is.False);
        }

        [Test]
        public void OneVsOneSetupUsesOneExplicitFleetShipInsteadOfRandomSquadSizes()
        {
            string source = ReadSource("Scripts", "Levels", "Level.RandomSquadSetup.cs");
            int methodStart = source.IndexOf("private void AddRlOneVsOneSquadForSetup", StringComparison.Ordinal);
            int methodEnd = source.IndexOf("private void AddRandomSquadsForSetup", methodStart, StringComparison.Ordinal);

            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, methodEnd - methodStart);
            Assert.That(method, Does.Contain("new FleetShip("));
            Assert.That(method, Does.Contain("new SquadShip(fleetShip, Vector2.zero)"));
            Assert.That(method, Does.Not.Contain("SetupRandomShips"));
            Assert.That(method, Does.Contain("ConfigData.ArmedShipTypes.Contains(type)"));
        }

        [Test]
        public void FirstProofEnvironmentDisablesExtraDimensionsAndRandomizesFacing()
        {
            string setup = ReadSource("Scripts", "Levels", "Level.Setup.cs");
            Assert.That(setup, Does.Contain("ConfigureTrainingMap(Map)"));
            Assert.That(setup, Does.Contain("CurrentLevelOptions.Mining = 0"));
            Assert.That(setup, Does.Contain("CurrentLevelOptions.AsteroidOption = 0"));
            Assert.That(setup, Does.Contain("CurrentLevelOptions.Obstacles = \"No\""));

            string squadSetup = ReadSource("Scripts", "Levels", "Level.RandomSquadSetup.cs");
            Assert.That(squadSetup, Does.Contain("RandomizeRlOneVsOneFacing(side)"));
            Assert.That(squadSetup, Does.Contain("Random.Range(0f, 360f)"));
            Assert.That(squadSetup, Does.Contain("ship.Rotation = ship.transform.eulerAngles.z"));
            Assert.That(squadSetup, Does.Contain("Rotation = ship.Turrets[turretIndex].PieceTransform.eulerAngles.z"));
        }

        [Test]
        public void RewardWeightsKeepVictoryDominant()
        {
            Type rewardType = RuntimeAssembly.GetType("RlOneVsOneReward");
            Assert.That(rewardType, Is.Not.Null);

            Assert.That(GetConstant(rewardType, "WinReward"), Is.EqualTo(10f));
            Assert.That(GetConstant(rewardType, "LossReward"), Is.EqualTo(-10f));
            Assert.That(GetConstant(rewardType, "TsvRewardScale"), Is.EqualTo(1f));
            Assert.That(GetConstant(rewardType, "MaximumEpisodeTimePenalty"), Is.EqualTo(0.1f));

            MethodInfo tsvReward = rewardType.GetMethod("CalculateTsvDeltaReward", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo timePenalty = rewardType.GetMethod("CalculateTimePenalty", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(tsvReward, Is.Not.Null);
            Assert.That(timePenalty, Is.Not.Null);

            float tsv = (float)tsvReward.Invoke(null, new object[] { 100, 80, 200, 150, 300 });
            float fullTimeoutPenalty = (float)timePenalty.Invoke(null, new object[] { 120f });
            Assert.That(tsv, Is.EqualTo(0.1f).Within(0.0001f));
            Assert.That(fullTimeoutPenalty, Is.EqualTo(-0.1f).Within(0.0001f));
        }

        [Test]
        public void TimePreferenceOnlyAppliesToTheWinner()
        {
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            Assert.That(coordinator, Does.Contain("winningSide == beeSide"));
            Assert.That(coordinator, Does.Contain("winningSide == humanSide"));
            Assert.That(coordinator, Does.Contain("? RlOneVsOneReward.CalculateTimePenalty(durationSeconds)"));
            Assert.That(coordinator, Does.Contain(": 0f;"));
        }

        [Test]
        public void PolicyUsesOneSharedBehaviorAndOnlyHiveMindEnemyKnowledge()
        {
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            Assert.That(agent, Does.Contain("BehaviorName = \"BeesRL1v1\""));
            Assert.That(agent, Does.Contain("ContinuousActionCount = 5"));
            Assert.That(agent, Does.Contain("CreateAgent(stage, ConfigData.Configuration.BeeSide, 0"));
            Assert.That(agent, Does.Contain("CreateAgent(stage, ConfigData.Configuration.HumanSide, 1"));
            Assert.That(agent, Does.Contain("GetShipsVisibleToHiveMind(_side)"));
            Assert.That(agent, Does.Not.Contain("GetAllEnemyShips("));
        }

        [Test]
        public void PolicyOwnsMovementAimAndFireWhileWeaponTimerOwnsRateOfFire()
        {
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            Assert.That(agent, Does.Contain("_ship.Direction = 360"));
            Assert.That(agent, Does.Contain("turret.SetRlControl(targetPoint, fireRequested)"));

            string aiming = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Aiming.cs");
            int rlAim = aiming.IndexOf("if (IsRlControlled)", StringComparison.Ordinal);
            int mouseAim = aiming.IndexOf("else if (IsFiringManually)", StringComparison.Ordinal);
            Assert.That(rlAim, Is.GreaterThanOrEqualTo(0));
            Assert.That(mouseAim, Is.GreaterThan(rlAim));
            Assert.That(aiming, Does.Contain("TargetPoint = RlTargetPoint"));

            string targeting = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");
            Assert.That(targeting, Does.Contain("if (IsRlControlled)"));
            Assert.That(targeting, Does.Contain("TargetingPasses >= PassesPerFire"));
            Assert.That(targeting, Does.Contain("RlFireRequested && IsAimedAtTarget"));
            Assert.That(targeting, Does.Contain("FireAtPoint();"));
        }

        [Test]
        public void FirstTrainerConfigMatchesSharedBehaviorAndUsesPpoSelfPlay()
        {
            string config = ReadSource("Training", "rl_1v1_config.yaml");
            Assert.That(config, Does.Contain("BeesRL1v1:"));
            Assert.That(config, Does.Contain("trainer_type: ppo"));
            Assert.That(config, Does.Contain("self_play:"));
            Assert.That(config, Does.Contain("max_steps: 500000"));
        }

        [Test]
        public void EpisodeCoordinatorExposesTrainerHookAndEliminationIsReportedBeforeReset()
        {
            Type coordinatorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeCoordinator");
            Assert.That(coordinatorType, Is.Not.Null);

            DefaultExecutionOrder executionOrder = coordinatorType.GetCustomAttribute<DefaultExecutionOrder>();
            Assert.That(executionOrder, Is.Not.Null);
            Assert.That(executionOrder.order, Is.LessThan(0));
            Assert.That(coordinatorType.GetEvent("EpisodeEnded", BindingFlags.Static | BindingFlags.NonPublic), Is.Not.Null);

            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            Assert.That(coordinator, Does.Contain("EpisodeEnded?.Invoke(LastEpisodeResult);"));
            Assert.That(coordinator, Does.Contain("int winningSide = DetermineWinner(level);"));

            string runtime = ReadSource("Scripts", "Levels", "Level.Runtime.cs");
            int report = runtime.IndexOf("RlOneVsOneEpisodeCoordinator.CompleteElimination(this);", StringComparison.Ordinal);
            int reset = runtime.IndexOf("ResetLevel(false);", StringComparison.Ordinal);
            Assert.That(report, Is.GreaterThanOrEqualTo(0));
            Assert.That(reset, Is.GreaterThan(report));
        }

        [Test]
        public void TimeoutIsReportedAsNoWinnerBeforeLevelTeardown()
        {
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            Assert.That(coordinator, Does.Contain("_active.CompleteEpisode(level, 0, true);"));

            string ending = ReadSource("Scripts", "Levels", "Level.Ending.cs");
            int report = ending.IndexOf("RlOneVsOneEpisodeCoordinator.CompleteTimeout(this);", StringComparison.Ordinal);
            int teardown = ending.IndexOf("SaveAndEnd();", report, StringComparison.Ordinal);
            Assert.That(report, Is.GreaterThanOrEqualTo(0));
            Assert.That(teardown, Is.GreaterThan(report));
        }

        [Test]
        public void TrainingSceneAssetExists()
        {
            string scenePath = ReadPath("Scenes", "RL 1v1 Training.unity");
            Assert.That(File.Exists(scenePath), Is.True);
        }

        private void ApplyBootstrap()
        {
            MethodInfo apply = _bootstrapType.GetMethod(
                "Apply",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(apply, Is.Not.Null);
            apply.Invoke(null, new object[] { _stage });
        }

        private object GetBootstrapConstant(string name)
        {
            return GetConstant(_bootstrapType, name);
        }

        private static object GetConstant(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing constant {name} on {type.FullName}");
            return field.GetValue(null);
        }

        private static string ReadSource(params string[] pathParts)
        {
            return File.ReadAllText(ReadPath(pathParts));
        }

        private static string ReadPath(params string[] pathParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < pathParts.Length; i++)
            {
                path = Path.Combine(path, pathParts[i]);
            }
            return path;
        }
    }
}
