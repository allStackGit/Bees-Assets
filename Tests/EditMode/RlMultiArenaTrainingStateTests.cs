using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlMultiArenaTrainingStateTests
    {
        private Type _levelType;
        private Type _mapStateType;
        private MethodInfo _setMapSizeForTests;
        private MethodInfo _getMapSize;
        private MethodInfo _getTrackedLevelCount;
        private MethodInfo _resetMapState;
        private GameObject _arenaAObject;
        private GameObject _arenaBObject;
        private Component _arenaA;
        private Component _arenaB;

        [SetUp]
        public void SetUp()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            _levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            _mapStateType = RuntimeAssembly.GetType("RlOneVsOneArenaMapSizeState");
            _setMapSizeForTests = _mapStateType.GetMethod("SetMapSizeForTests", flags);
            _getMapSize = _mapStateType.GetMethod("GetMapSize", flags);
            _getTrackedLevelCount = _mapStateType.GetMethod("GetTrackedLevelCountForTests", flags);
            _resetMapState = _mapStateType.GetMethod("ResetForTests", flags);

            Assert.That(_setMapSizeForTests, Is.Not.Null);
            Assert.That(_getMapSize, Is.Not.Null);
            Assert.That(_getTrackedLevelCount, Is.Not.Null);
            Assert.That(_resetMapState, Is.Not.Null);

            _resetMapState.Invoke(null, null);
            _arenaAObject = new GameObject("RL map-state arena A");
            _arenaBObject = new GameObject("RL map-state arena B");
            _arenaA = _arenaAObject.AddComponent(_levelType);
            _arenaB = _arenaBObject.AddComponent(_levelType);
        }

        [TearDown]
        public void TearDown()
        {
            _resetMapState?.Invoke(null, null);
            if (_arenaAObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_arenaAObject);
            }
            if (_arenaBObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_arenaBObject);
            }
        }

        [Test]
        public void MapSizeStateIsStoredPerArena()
        {
            _setMapSizeForTests.Invoke(null, new object[] { _arenaA, 64f });
            _setMapSizeForTests.Invoke(null, new object[] { _arenaB, 128f });

            Assert.That((float)_getMapSize.Invoke(null, new object[] { _arenaA }), Is.EqualTo(64f));
            Assert.That((float)_getMapSize.Invoke(null, new object[] { _arenaB }), Is.EqualTo(128f));
            Assert.That((int)_getTrackedLevelCount.Invoke(null, null), Is.EqualTo(2));
        }

        [Test]
        public void MultiArenaRuntimeAppliesDurabilityToAdditionalLevels()
        {
            string source = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingDurabilityGuard.cs");
            Assert.That(source, Does.Contain("IReadOnlyList<Level> levels = _stage.Levels;"));
            Assert.That(source, Does.Contain("for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)"));
            Assert.That(source, Does.Contain("ApplyTrainingDurability(ships[shipIndex]);"));
        }

        [Test]
        public void LevelSetupUsesArenaLocalMapAndMatchupState()
        {
            string levelSetup = ReadSource("Scripts", "Levels", "Level.Setup.cs");
            string squadSetup = ReadSource("Scripts", "Levels", "Level.RandomSquadSetup.cs");

            Assert.That(levelSetup, Does.Contain("RlOneVsOneArenaMapSizeState.ConfigureTrainingMap(this, Map);"));
            Assert.That(squadSetup, Does.Contain("RlOneVsOnePerArenaMatchups.PrepareEpisode(this);"));
            Assert.That(squadSetup, Does.Contain("RlOneVsOnePerArenaMatchups.GetShipType(this, side, shipIndex);"));
            Assert.That(squadSetup, Does.Contain("RlOneVsOneArenaMapSizeState.GetSpawnRadius(this);"));
            Assert.That(squadSetup, Does.Contain("RlOneVsOneArenaMapSizeState.GetShipFormationOffset(this, shipIndex);"));
        }

        [Test]
        public void LegacyRangeMapSizeNoLongerOwnsMutableProcessEpisodeState()
        {
            string options = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingOptions.cs");

            Assert.That(options, Does.Not.Contain("_sampledMapSizeValid"));
            Assert.That(options, Does.Not.Contain("RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded"));
            Assert.That(options, Does.Contain("internal float MapSize => HasMapSizeRange ? _mapSizeMinimum : _mapSize;"));
        }

        private static string ReadSource(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}
