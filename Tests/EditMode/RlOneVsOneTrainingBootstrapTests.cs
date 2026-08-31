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
            ApplyBootstrap();

            Assert.That(RuntimeAssembly.GetField(_stage, "IsTrainingHiveMind"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "IsTrainingNueralNetwork"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_stage, "ActivateHiveMind"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "ActivateBrains"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "DoesUserHaveController"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "UseFullyRandomSquads"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_stage, "HasRandomizedOptions"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_stage, "IsRendering"), Is.True);
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
            return field.GetRawConstantValue();
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
