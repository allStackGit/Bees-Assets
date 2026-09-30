using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
            Assert.That(GetBootstrapConstant("TrainingMapSize"), Is.EqualTo(30f));
            Assert.That(GetBootstrapConstant("SpawnRadius"), Is.EqualTo(7.5f));
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
        public void TrainingStaticObstacleLayoutStaysUnderAreaBudgetAndCannotPartitionTheArena()
        {
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            MethodInfo buildLayout = levelType.GetMethod(
                "BuildRlTrainingObstacleLayout",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(buildLayout, Is.Not.Null);

            const float min = -11f;
            const float max = 11f;
            List<Rect> layout = (List<Rect>)buildLayout.Invoke(
                null,
                new object[] { min, max, min, max, 12345, 0f });

            Assert.That(layout, Is.Not.Empty);
            float playableArea = (max - min) * (max - min);
            float obstacleArea = 0f;
            float corridorHalfWidth = (max - min) * 0.25f;
            for (int i = 0; i < layout.Count; i++)
            {
                Rect obstacle = layout[i];
                obstacleArea += obstacle.width * obstacle.height;

                Assert.That(
                    obstacle.xMax <= -corridorHalfWidth || obstacle.xMin >= corridorHalfWidth,
                    Is.True,
                    "Every static obstacle must stay outside the full-height central corridor.");
                Assert.That(
                    obstacle.yMax <= -corridorHalfWidth || obstacle.yMin >= corridorHalfWidth,
                    Is.True,
                    "Every static obstacle must stay outside the full-width central corridor.");
            }

            Assert.That(obstacleArea, Is.LessThanOrEqualTo(playableArea * 0.25f + 0.001f));

            List<Rect> oversizedShipLayout = (List<Rect>)buildLayout.Invoke(
                null,
                new object[] { min, max, min, max, 12345, 10.5f });
            Assert.That(oversizedShipLayout, Is.Empty,
                "When the selected ships need nearly the full arena width, obstacle generation must yield rather than block movement.");
        }

        [Test]
        public void RotationSafeSpawnSizingRaisesLargeShipEpisodesWithinConfiguredRange()
        {
            Type mapStateType = RuntimeAssembly.GetType("RlOneVsOneArenaMapSizeState");
            Type shipTypesType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            MethodInfo requiredSize = mapStateType.GetMethod(
                "GetMinimumSafeMapSizeForShip",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(requiredSize, Is.Not.Null);

            object beehive = Enum.Parse(shipTypesType, "Beehive");
            float minimum = (float)requiredSize.Invoke(null, new[] { beehive });

            Assert.That(minimum, Is.GreaterThan(48f),
                "A Beehive must not be sampled onto the 48-unit map where its rotation-safe footprint cannot fit.");
            Assert.That(minimum, Is.LessThanOrEqualTo(64f),
                "The current 48..64 curriculum must still be able to train Beehive episodes at its safe upper step.");
        }

        [Test]
        public void RewardWeightsKeepVictoryDominant()
        {
            Type rewardType = RuntimeAssembly.GetType("RlOneVsOneReward");
            Assert.That(rewardType, Is.Not.Null);

            Assert.That(GetConstant(rewardType, "WinReward"), Is.EqualTo(1f));
            Assert.That(GetConstant(rewardType, "LossReward"), Is.EqualTo(-1f));
            Assert.That(GetConstant(rewardType, "TimeoutReward"), Is.EqualTo(-1.1f));
            Assert.That(GetConstant(rewardType, "TsvRewardScale"), Is.EqualTo(0.1f));
            Assert.That(GetConstant(rewardType, "MaximumEpisodeTimePenalty"), Is.EqualTo(0.01f));

            MethodInfo immediateTsvReward = rewardType.GetMethod("CalculateTsvLossReward", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo tsvReward = rewardType.GetMethod("CalculateTsvDeltaReward", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo timePenalty = rewardType.GetMethod("CalculateTimePenalty", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(immediateTsvReward, Is.Not.Null);
            Assert.That(tsvReward, Is.Not.Null);
            Assert.That(timePenalty, Is.Not.Null);

            float immediate = (float)immediateTsvReward.Invoke(null, new object[] { 30, 300 });
            float tsv = (float)tsvReward.Invoke(null, new object[] { 100, 80, 200, 150, 300 });
            float fullTimeoutPenalty = (float)timePenalty.Invoke(null, new object[] { 120f });
            Assert.That(immediate, Is.EqualTo(0.01f).Within(0.0001f));
            Assert.That(tsv, Is.EqualTo(0.01f).Within(0.0001f));
            Assert.That(fullTimeoutPenalty, Is.EqualTo(-0.01f).Within(0.0001f));
        }

        [Test]
        public void TrainingSceneAssetExists()
        {
            string scenePath = ReadPath("Scenes", "RL 1v1 Training.unity");
            Assert.That(File.Exists(scenePath), Is.True);
        }

        [Test]
        public void TrainingSceneProvidesStaticObstaclePoolPrefabs()
        {
            const string scenePath = "Assets/Scenes/RL 1v1 Training.unity";
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                Type prefabsType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Prefabs");
                Component prefabs = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    prefabs = root.GetComponentInChildren(prefabsType, true);
                    if (prefabs != null)
                    {
                        break;
                    }
                }

                Assert.That(prefabs, Is.Not.Null);
                Assert.That(RuntimeAssembly.GetField(prefabs, "ObstaclePrefab"), Is.Not.Null);
                Assert.That(RuntimeAssembly.GetField(prefabs, "ObstacleBackgroundPrefab"), Is.Not.Null);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private void ApplyBootstrap()
        {
            MethodInfo apply = _bootstrapType.GetMethod(
                "Apply",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(apply, Is.Not.Null);

            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object previousConfiguration = RuntimeAssembly.GetStaticField(configDataType, "Configuration");
            object previousStartingSettings = RuntimeAssembly.GetStaticField(configDataType, "StartingSettings");
            object previousShipInfo = RuntimeAssembly.GetStaticField(configDataType, "ShipInfo");
            try
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", CreateLoadedSetting("Assets.Scripts.Settings.Configuration"));
                RuntimeAssembly.SetStaticField(configDataType, "StartingSettings", CreateLoadedSetting("Assets.Scripts.Settings.StartingSettings"));
                RuntimeAssembly.SetStaticField(configDataType, "ShipInfo", CreateLoadedSetting("Assets.Scripts.Settings.ShipStats"));
                apply.Invoke(null, new object[] { _stage });
            }
            finally
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", previousConfiguration);
                RuntimeAssembly.SetStaticField(configDataType, "StartingSettings", previousStartingSettings);
                RuntimeAssembly.SetStaticField(configDataType, "ShipInfo", previousShipInfo);
            }
        }

        private static object CreateLoadedSetting(string typeName)
        {
            object setting = RuntimeAssembly.CreateUninitialized(typeName);
            RuntimeAssembly.SetField(setting, "IsLoaded", true);
            return setting;
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
