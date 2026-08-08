using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesPerformanceQualification")]
    public class DenseObstacleQualificationTests
    {
        private const int RequestCount = 20;
        private const double SetupBudgetMilliseconds = 2000;
        private const double P95BudgetMilliseconds = 750;

        private readonly List<GameObject> _obstacleObjects = new List<GameObject>();
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

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _originalMaxThreads = (int)RuntimeAssembly.GetStaticField(_configDataType, "MaxThreads");
            RuntimeAssembly.SetStaticField(_configDataType, "MaxThreads", 1);

            _stageObject = new GameObject(nameof(DenseObstacleQualificationTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            RuntimeAssembly.SetField(_stage, "IsTraining", true);
            ((Behaviour)_stage).enabled = false;

            _levelObject = new GameObject(nameof(DenseObstacleQualificationTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            ((Behaviour)_level).enabled = false;
            RuntimeAssembly.SetField(_level, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            RuntimeAssembly.SetField(_state, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "MapWidth", 256);
            RuntimeAssembly.SetField(_level, "MapHeight", 256);
            RuntimeAssembly.SetField(_level, "HalfMapWidth", 128);
            RuntimeAssembly.SetField(_level, "HalfMapHeight", 128);
            RuntimeAssembly.SetField(_level, "HasObstacles", true);

            BuildDenseStaticObstacleField();
            Physics2D.SyncTransforms();

            _shipObject = new GameObject(nameof(DenseObstacleQualificationTests) + " Ship");
            _ship = _shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Honeybee"));
            ((Behaviour)_ship).enabled = false;
            RuntimeAssembly.SetField(_ship, "Transform", _shipObject.transform);
            RuntimeAssembly.SetField(_ship, "Level", _level);
            RuntimeAssembly.SetField(_ship, "Stage", _stage);
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "MaxThreads", _originalMaxThreads);
            foreach (GameObject obstacleObject in _obstacleObjects)
            {
                UnityEngine.Object.DestroyImmediate(obstacleObject);
            }
            UnityEngine.Object.DestroyImmediate(_shipObject);
            UnityEngine.Object.DestroyImmediate(_levelObject);
            UnityEngine.Object.DestroyImmediate(_stageObject);
            _obstacleObjects.Clear();
        }

        [UnityTest]
        public IEnumerator DenseStaticObstaclesSupportSmallAndLargeClearanceRequestsWithinBudget()
        {
            Stopwatch setup = Stopwatch.StartNew();
            _pathfinder = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Pathfinder"),
                new[] { _level });
            setup.Stop();
            RuntimeAssembly.SetField(_level, "Pathfinder", _pathfinder);

            Assert.That(setup.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(SetupBudgetMilliseconds),
                "Dense obstacle map setup exceeded the qualification budget.");

            var elapsed = new List<double>(RequestCount);
            for (int requestIndex = 0; requestIndex < RequestCount; requestIndex++)
            {
                int startX = 5 + (requestIndex % 5);
                int startY = 6 + ((requestIndex * 3) % 10);
                int endX = 54 + (requestIndex % 4);
                int endY = 48 + ((requestIndex * 5) % 10);
                int clearance = requestIndex % 2 == 0 ? 1 : 3;

                RuntimeAssembly.SetField(_ship, "PathfindingThreadComplete", false);
                RuntimeAssembly.SetField(_ship, "PathfindingValue", null);
                Stopwatch timer = Stopwatch.StartNew();
                RuntimeAssembly.Invoke(_pathfinder, "FindPath", _ship,
                    startX, startY, endX, endY, clearance);
                int requestId = (int)RuntimeAssembly.GetField(_ship, "PathfindingRequestId");

                bool completed = false;
                for (int poll = 0; poll < 7500; poll++)
                {
                    RuntimeAssembly.Invoke(_pathfinder, "Update");
                    if ((bool)RuntimeAssembly.GetField(_ship, "PathfindingThreadComplete") &&
                        (int)RuntimeAssembly.GetField(_ship, "PathfindingCompletedRequestId") == requestId)
                    {
                        completed = true;
                        break;
                    }
                    yield return null;
                }
                timer.Stop();

                Assert.That(completed, Is.True,
                    $"Dense qualification request {requestIndex} (clearance {clearance}) did not complete.");
                Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingValue"), Is.Not.Null,
                    $"Dense qualification request {requestIndex} (clearance {clearance}) found no route.");
                elapsed.Add(timer.Elapsed.TotalMilliseconds);
            }

            double[] sorted = elapsed.OrderBy(value => value).ToArray();
            double p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
            UnityEngine.Debug.Log(
                $"PERF dense-pathfinder setup={setup.Elapsed.TotalMilliseconds:F2}ms " +
                $"median={sorted[sorted.Length / 2]:F2}ms p95={p95:F2}ms max={sorted.Last():F2}ms " +
                $"requests={RequestCount} obstacles={_obstacleObjects.Count} grid=64x64 workers=1 clearances=1,3");
            Assert.That(p95, Is.LessThanOrEqualTo(P95BudgetMilliseconds),
                "Dense-obstacle pathfinding p95 exceeded the qualification regression budget.");
        }

        private void BuildDenseStaticObstacleField()
        {
            Type obstacleType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Obstacle");
            Type obstacleEnumType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ObstacleTypes");
            object staticObstacle = Enum.Parse(obstacleEnumType, "StaticObstacle");

            int id = 1;
            float[] rows = { -72f, -36f, 0f, 36f, 72f };
            foreach (float y in rows)
            {
                for (float x = -104f; x <= 104f; x += 16f)
                {
                    // Leave alternating wide corridors so clearance-3 ships still have a legal route.
                    bool centralGap = Mathf.Abs(x) <= 20f;
                    bool sideGap = ((int)((y + 72f) / 36f) % 2 == 0) && x >= 52f && x <= 84f;
                    if (centralGap || sideGap)
                    {
                        continue;
                    }

                    GameObject obstacleObject = new GameObject("Dense obstacle " + id);
                    _obstacleObjects.Add(obstacleObject);
                    obstacleObject.transform.localPosition = new Vector2(x, y);
                    BoxCollider2D collider = obstacleObject.AddComponent<BoxCollider2D>();
                    collider.size = new Vector2(10f, 10f);
                    object obstacle = obstacleObject.AddComponent(obstacleType);
                    RuntimeAssembly.SetField(obstacle, "Id", id++);
                    RuntimeAssembly.SetField(obstacle, "Name", obstacleObject.name);
                    RuntimeAssembly.SetField(obstacle, "ObstacleType", staticObstacle);
                    RuntimeAssembly.SetField(obstacle, "Level", _level);
                    RuntimeAssembly.SetField(obstacle, "Stage", _stage);
                    RuntimeAssembly.SetField(obstacle, "Collider", collider);
                    RuntimeAssembly.SetField(obstacle, "ClearanceMappingCollider", collider);
                    RuntimeAssembly.Invoke(_state, "AddObstacle", obstacle);
                }
            }
        }
    }
}
