using System;
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

    }
}
