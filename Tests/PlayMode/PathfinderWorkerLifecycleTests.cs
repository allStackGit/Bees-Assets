using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesPlayModeFoundation")]
    public class PathfinderWorkerLifecycleTests
    {
        private GameObject _stageObject;
        private GameObject _levelObject;
        private GameObject _shipObject;
        private object _stage;
        private object _level;
        private object _state;
        private object _ship;
        private object _pathfinder;
        private Type _configDataType;
        private int _originalMaxThreads;
        private object _originalConfiguration;

        [SetUp]
        public void SetUp()
        {
            _stageObject = new GameObject(nameof(PathfinderWorkerLifecycleTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            ((Behaviour)_stage).enabled = false;

            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _originalMaxThreads = (int)RuntimeAssembly.GetStaticField(_configDataType, "MaxThreads");
            _originalConfiguration = RuntimeAssembly.GetStaticField(_configDataType, "Configuration");
            RuntimeAssembly.SetStaticField(_configDataType, "MaxThreads", 2);
            object testConfiguration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(testConfiguration, "UserSide", 1);
            RuntimeAssembly.SetField(testConfiguration, "AISide", 2);
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", testConfiguration);

            _levelObject = new GameObject(nameof(PathfinderWorkerLifecycleTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            ((Behaviour)_level).enabled = false;
            RuntimeAssembly.SetField(_level, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            RuntimeAssembly.SetField(_state, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "MapWidth", 64);
            RuntimeAssembly.SetField(_level, "MapHeight", 64);
            RuntimeAssembly.SetField(_level, "HalfMapWidth", 32);
            RuntimeAssembly.SetField(_level, "HalfMapHeight", 32);
            RuntimeAssembly.SetField(_level, "HasObstacles", true);

            _shipObject = new GameObject(nameof(PathfinderWorkerLifecycleTests) + " Ship");
            _ship = _shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Honeybee"));
            ((Behaviour)_ship).enabled = false;
            RuntimeAssembly.SetField(_ship, "Transform", _shipObject.transform);
            RuntimeAssembly.SetField(_ship, "Level", _level);
            RuntimeAssembly.SetField(_ship, "Stage", _stage);

            _pathfinder = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Pathfinder"),
                new[] { _level });
            RuntimeAssembly.SetField(_level, "Pathfinder", _pathfinder);
        }

        [TearDown]
        public void TearDown()
        {
            if (_configDataType != null)
            {
                RuntimeAssembly.SetStaticField(_configDataType, "MaxThreads", _originalMaxThreads);
                RuntimeAssembly.SetStaticField(_configDataType, "Configuration", _originalConfiguration);
            }
            if (_shipObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_shipObject);
            }
            if (_levelObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_levelObject);
            }
            if (_stageObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_stageObject);
            }
        }

        [UnityTest]
        public IEnumerator RealWorkerPublishesOnlyNewestRequestAndRejectsPreviousLifecycle()
        {
            RuntimeAssembly.Invoke(_pathfinder, "FindPath", _ship, 1, 1, 12, 12, 1);
            int firstRequest = (int)RuntimeAssembly.GetField(_ship, "PathfindingRequestId");

            RuntimeAssembly.Invoke(_pathfinder, "FindPath", _ship, 2, 2, 10, 4, 1);
            int newestRequest = (int)RuntimeAssembly.GetField(_ship, "PathfindingRequestId");
            Assert.That(newestRequest, Is.GreaterThan(firstRequest));

            yield return WaitForAcceptedPath(newestRequest);

            object newestPath = RuntimeAssembly.GetField(_ship, "PathfindingValue");
            Assert.That(newestPath, Is.Not.Null);
            Assert.That(RuntimeAssembly.GetField(newestPath, "StartX"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetField(newestPath, "StartY"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetField(newestPath, "EndX"), Is.EqualTo(10));
            Assert.That(RuntimeAssembly.GetField(newestPath, "EndY"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingCompletedRequestId"), Is.EqualTo(newestRequest));

            int previousLifecycle = (int)RuntimeAssembly.GetField(_ship, "PathfindingLifecycleId");
            RuntimeAssembly.Invoke(_pathfinder, "FindPath", _ship, 3, 3, 14, 14, 1);
            int discardedRequest = (int)RuntimeAssembly.GetField(_ship, "PathfindingRequestId");
            RuntimeAssembly.Invoke(_ship, "ClearData");
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingLifecycleId"),
                Is.EqualTo(unchecked(previousLifecycle + 1)));

            for (int frame = 0; frame < 300; frame++)
            {
                RuntimeAssembly.Invoke(_pathfinder, "Update");
                yield return new WaitForSecondsRealtime(0.01f);
            }

            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingValue"), Is.Null,
                $"Request {discardedRequest} from the previous pooled lifecycle published a path.");
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingCompletedRequestId"), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingThreadComplete"), Is.False);
        }

        [UnityTest]
        public IEnumerator PathfinderOwnershipPreventsShipPoolReuseUntilWorkerReleasesLifecycle()
        {
            int lifecycleId = (int)RuntimeAssembly.GetField(_ship, "PathfindingLifecycleId");
            RuntimeAssembly.Invoke(_pathfinder, "FindPath", _ship, 1, 1, 14, 14, 1);

            Assert.That(RuntimeAssembly.Invoke(
                _pathfinder, "HasOutstandingWorkForShip", _ship, lifecycleId), Is.EqualTo(true));
            Assert.That(RuntimeAssembly.Invoke(_ship, "CanReturnToPool"), Is.EqualTo(false),
                "A ship wrapper must not be reusable while a pathfinder worker still owns it.");

            for (int frame = 0; frame < 500; frame++)
            {
                RuntimeAssembly.Invoke(_pathfinder, "Update");
                if (!(bool)RuntimeAssembly.Invoke(
                        _pathfinder, "HasOutstandingWorkForShip", _ship, lifecycleId))
                {
                    Assert.That(RuntimeAssembly.Invoke(_ship, "CanReturnToPool"), Is.EqualTo(true));
                    yield break;
                }
                yield return new WaitForSecondsRealtime(0.01f);
            }

            Assert.Fail("Pathfinder worker retained ship lifecycle ownership for more than five seconds.");
        }

        private IEnumerator WaitForAcceptedPath(int requestId)
        {
            for (int frame = 0; frame < 500; frame++)
            {
                RuntimeAssembly.Invoke(_pathfinder, "Update");
                if ((bool)RuntimeAssembly.GetField(_ship, "PathfindingThreadComplete") &&
                    (int)RuntimeAssembly.GetField(_ship, "PathfindingCompletedRequestId") == requestId)
                {
                    yield break;
                }
                yield return new WaitForSecondsRealtime(0.01f);
            }

            bool[] active = (bool[])RuntimeAssembly.GetField(_pathfinder, "IsThreadActive");
            int[] requestIds = (int[])RuntimeAssembly.GetField(_pathfinder, "RequestIds");
            int[] lifecycleIds = (int[])RuntimeAssembly.GetField(_pathfinder, "LifecycleIds");
            Assert.Fail(
                $"Pathfinding request {requestId} did not complete within five seconds. " +
                $"Ship request/lifecycle/completed={RuntimeAssembly.GetField(_ship, "PathfindingRequestId")}/" +
                $"{RuntimeAssembly.GetField(_ship, "PathfindingLifecycleId")}/" +
                $"{RuntimeAssembly.GetField(_ship, "PathfindingCompletedRequestId")}; " +
                $"active={string.Join(",", active)}; requests={string.Join(",", requestIds)}; " +
                $"lifecycles={string.Join(",", lifecycleIds)}; " +
                $"completed queue={RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_pathfinder, "_completedPaths"))}.");
        }
    }
}
