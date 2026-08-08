using System;
using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesPerformanceQualification")]
    public class DynamicObstacleQualificationTests
    {
        private GameObject _stageObject;
        private GameObject _levelObject;
        private GameObject _shipObject;
        private GameObject _asteroidObject;
        private object _stage;
        private object _level;
        private object _state;
        private object _ship;
        private object _asteroid;
        private object _pathfinder;
        private Type _configDataType;
        private int _originalMaxThreads;

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _originalMaxThreads = (int)RuntimeAssembly.GetStaticField(_configDataType, "MaxThreads");
            RuntimeAssembly.SetStaticField(_configDataType, "MaxThreads", 1);

            _stageObject = new GameObject(nameof(DynamicObstacleQualificationTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            RuntimeAssembly.SetField(_stage, "IsTraining", true);
            RuntimeAssembly.SetField(_stage, "FixedUpdates", 1);
            ((Behaviour)_stage).enabled = false;

            _levelObject = new GameObject(nameof(DynamicObstacleQualificationTests) + " Level");
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
            RuntimeAssembly.SetField(_level, "ActivateCollisionAsteroids", true);

            _shipObject = new GameObject(nameof(DynamicObstacleQualificationTests) + " Ship");
            _ship = _shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Honeybee"));
            ((Behaviour)_ship).enabled = false;
            RuntimeAssembly.SetField(_ship, "Transform", _shipObject.transform);
            RuntimeAssembly.SetField(_ship, "Level", _level);
            RuntimeAssembly.SetField(_ship, "Stage", _stage);

            // Production creates the base Pathfinder before collision asteroids are spawned.
            _pathfinder = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Pathfinder"),
                new[] { _level });
            RuntimeAssembly.SetField(_level, "Pathfinder", _pathfinder);

            // Mirror the registration performed by CollisionAsteroid.Setup without invoking
            // its unrelated map-parenting/random-spawn logic in this isolated fixture.
            _asteroidObject = new GameObject("Qualification moving asteroid");
            _asteroidObject.tag = "Obstacle";
            _asteroidObject.transform.localPosition = Vector2.zero;
            BoxCollider2D collider = _asteroidObject.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(24f, 24f);
            Rigidbody2D body = _asteroidObject.AddComponent<Rigidbody2D>();
            body.gravityScale = 0f;
            body.linearVelocity = new Vector2(2f, 0f);
            _asteroid = _asteroidObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.CollisionAsteroid"));
            RuntimeAssembly.SetField(_asteroid, "Id", 1);
            RuntimeAssembly.SetField(_asteroid, "Name", "Qualification moving asteroid");
            RuntimeAssembly.SetField(_asteroid, "ObstacleType", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ObstacleTypes"), "CollisionAsteroid"));
            RuntimeAssembly.SetField(_asteroid, "Level", _level);
            RuntimeAssembly.SetField(_asteroid, "Stage", _stage);
            RuntimeAssembly.SetField(_asteroid, "Collider", collider);
            RuntimeAssembly.SetField(_asteroid, "ClearanceMappingCollider", collider);
            RuntimeAssembly.SetField(_asteroid, "Body", body);
            RuntimeAssembly.Invoke(_state, "AddObstacle", _asteroid);
            int mapPointsIndex = (int)RuntimeAssembly.Invoke(_pathfinder, "AddObstacle", _asteroid);
            RuntimeAssembly.SetField(_asteroid, "MapPointsIndex", mapPointsIndex);
            Physics2D.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "MaxThreads", _originalMaxThreads);
            UnityEngine.Object.DestroyImmediate(_shipObject);
            UnityEngine.Object.DestroyImmediate(_asteroidObject);
            UnityEngine.Object.DestroyImmediate(_levelObject);
            UnityEngine.Object.DestroyImmediate(_stageObject);
        }

        [UnityTest]
        public IEnumerator MovingObstacleLayerTracksAsteroidAcrossFixedStepsAndRefreshesWithinBudget()
        {
            Assert.That(RuntimeAssembly.Invoke(_pathfinder, "CanOccupyDestination", Vector2.zero, 1), Is.False,
                "The moving asteroid should initially block its occupied destination.");
            Assert.That(RuntimeAssembly.Invoke(_pathfinder, "CanOccupyDestination", new Vector2(80f, 0f), 1), Is.True);

            _asteroidObject.transform.localPosition = new Vector2(80f, 0f);
            Physics2D.SyncTransforms();
            RuntimeAssembly.SetField(_stage, "FixedUpdates", 2);
            yield return null;

            Assert.That(RuntimeAssembly.Invoke(_pathfinder, "CanOccupyDestination", Vector2.zero, 1), Is.True,
                "The old asteroid position should be released on the next dynamic-layer frame.");
            Assert.That(RuntimeAssembly.Invoke(_pathfinder, "CanOccupyDestination", new Vector2(80f, 0f), 1), Is.False,
                "The new asteroid position should become blocked on the next dynamic-layer frame.");

            Stopwatch timer = Stopwatch.StartNew();
            for (int index = 0; index < 100; index++)
            {
                _asteroidObject.transform.localPosition = new Vector2(-80f + (index % 20) * 8f, 40f);
                Physics2D.SyncTransforms();
                RuntimeAssembly.SetField(_stage, "FixedUpdates", 3 + index);
                RuntimeAssembly.Invoke(_pathfinder, "CanOccupyDestination", new Vector2(0f, -40f), 1);
            }
            timer.Stop();

            UnityEngine.Debug.Log(
                $"PERF dynamic-obstacle refreshes=100 total={timer.Elapsed.TotalMilliseconds:F2}ms " +
                $"average={timer.Elapsed.TotalMilliseconds / 100:F3}ms");
            Assert.That(timer.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(1000),
                "100 real moving-obstacle layer refreshes exceeded the regression budget.");
        }
    }
}
