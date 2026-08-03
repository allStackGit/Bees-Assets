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
    public class PerformanceQualificationTests
    {
        public const int PathRequestCount = 25;
        public const double MapSetupBudgetMilliseconds = 1000;
        public const double PathP95BudgetMilliseconds = 250;
        public const int RuntimeResetIterations = 10000;
        public const double RuntimeResetBudgetMilliseconds = 1500;

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

            _stageObject = new GameObject(nameof(PerformanceQualificationTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            ((Behaviour)_stage).enabled = false;

            _levelObject = new GameObject(nameof(PerformanceQualificationTests) + " Level");
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

            _shipObject = new GameObject(nameof(PerformanceQualificationTests) + " Ship");
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
            UnityEngine.Object.DestroyImmediate(_shipObject);
            UnityEngine.Object.DestroyImmediate(_levelObject);
            UnityEngine.Object.DestroyImmediate(_stageObject);
        }

        [UnityTest]
        public IEnumerator OpenGridPathfinderMeetsOldHardwareQualificationBudget()
        {
            Stopwatch setup = Stopwatch.StartNew();
            _pathfinder = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Pathfinder"),
                new[] { _level });
            setup.Stop();
            RuntimeAssembly.SetField(_level, "Pathfinder", _pathfinder);
            Assert.That(setup.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(MapSetupBudgetMilliseconds),
                "Pathfinder map setup exceeded the qualification budget.");

            var elapsed = new List<double>(PathRequestCount);
            for (int requestIndex = 0; requestIndex < PathRequestCount; requestIndex++)
            {
                int startX = 2 + (requestIndex % 8);
                int startY = 2 + ((requestIndex * 3) % 8);
                int endX = 45 + (requestIndex % 12);
                int endY = 45 + ((requestIndex * 5) % 12);
                RuntimeAssembly.SetField(_ship, "PathfindingThreadComplete", false);
                RuntimeAssembly.SetField(_ship, "PathfindingValue", null);

                Stopwatch requestTimer = Stopwatch.StartNew();
                RuntimeAssembly.Invoke(_pathfinder, "FindPath", _ship, startX, startY, endX, endY, 1);
                int requestId = (int)RuntimeAssembly.GetField(_ship, "PathfindingRequestId");

                bool completed = false;
                for (int poll = 0; poll < 5000; poll++)
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
                requestTimer.Stop();

                Assert.That(completed, Is.True, $"Qualification path {requestIndex} did not complete.");
                Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingValue"), Is.Not.Null,
                    $"Qualification path {requestIndex} found no route on an open grid.");
                elapsed.Add(requestTimer.Elapsed.TotalMilliseconds);
            }

            double[] sorted = elapsed.OrderBy(value => value).ToArray();
            double p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
            UnityEngine.Debug.Log(
                $"PERF pathfinder map={setup.Elapsed.TotalMilliseconds:F2}ms " +
                $"median={sorted[sorted.Length / 2]:F2}ms p95={p95:F2}ms max={sorted.Last():F2}ms " +
                $"requests={PathRequestCount} grid=64x64 workers=1");
            Assert.That(p95, Is.LessThanOrEqualTo(PathP95BudgetMilliseconds),
                "Open-grid pathfinding p95 exceeded the old-hardware qualification budget.");
        }

        [Test]
        public void RepeatedRuntimeStateResetMeetsCpuQualificationBudget()
        {
            Stopwatch timer = Stopwatch.StartNew();
            for (int index = 0; index < RuntimeResetIterations; index++)
            {
                RuntimeAssembly.Invoke(_state, "ResetState");
            }
            timer.Stop();

            UnityEngine.Debug.Log(
                $"PERF state-reset total={timer.Elapsed.TotalMilliseconds:F2}ms " +
                $"iterations={RuntimeResetIterations} average={timer.Elapsed.TotalMilliseconds / RuntimeResetIterations:F4}ms");
            Assert.That(timer.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(RuntimeResetBudgetMilliseconds),
                "Repeated GameState reset exceeded the old-hardware CPU qualification budget.");
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Projectiles")), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(_state, "GameOver"), Is.False);
        }
    }
}
