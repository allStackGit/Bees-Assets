using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    public class LevelLifecycleSoakTests
    {
        private const int CycleCount = 100;
        private const int ExtendedCycleCount = 1000;

        private GameObject _stageObject;
        private GameObject _levelObject;
        private GameObject _shipObject;
        private Component _stage;
        private Component _level;
        private Component _state;
        private object _pool;
        private object _ship;
        private Type _configDataType;
        private object _originalConfiguration;
        private readonly HashSet<long> _globalHandledRequests = new HashSet<long>();

        [SetUp]
        public void SetUp()
        {
            _stageObject = new GameObject(nameof(LevelLifecycleSoakTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            Component prefabs = _stageObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Prefabs"));
            _pool = _stageObject.AddComponent(RuntimeAssembly.GetType("Pool"));
            RuntimeAssembly.SetField(_stage, "Prefabs", prefabs);
            RuntimeAssembly.SetField(_stage, "Pool", _pool);
            RuntimeAssembly.SetField(_stage, "IsTraining", true);
            RuntimeAssembly.SetField(_stage, "IsRendering", false);
            RuntimeAssembly.SetField(_stage, "DoesUserHaveController", true);
            ((Behaviour)_stage).enabled = false;
            RuntimeAssembly.Invoke(_pool, "Setup", _stage);

            _levelObject = new GameObject(nameof(LevelLifecycleSoakTests) + " Level");
            _level = _levelObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(_level, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            RuntimeAssembly.SetField(_state, "Stage", _stage);
            ((Behaviour)_level).enabled = false;

            object levelOptions = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions"),
                new object[] { 1, 2, "Lifecycle soak" });
            RuntimeAssembly.SetField(_level, "CurrentLevelOptions", levelOptions);

            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _originalConfiguration = RuntimeAssembly.GetStaticField(
                _configDataType, "Configuration");
            object testConfiguration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(testConfiguration, "UserSide", 1);
            RuntimeAssembly.SetField(testConfiguration, "AISide", 2);
            RuntimeAssembly.SetField(testConfiguration, "HumanSide", 1);
            RuntimeAssembly.SetField(testConfiguration, "BeeSide", 2);
            RuntimeAssembly.SetStaticField(
                _configDataType, "Configuration", testConfiguration);
            Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "_socket"), Is.Null,
                "Reading configuration must not create the global game socket.");

            _shipObject = new GameObject(nameof(LevelLifecycleSoakTests) + " Honeybee");
            _ship = _shipObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Honeybee"));
            ((Behaviour)_ship).enabled = false;
            RuntimeAssembly.SetField(_ship, "Transform", _shipObject.transform);
            RuntimeAssembly.SetField(_ship, "Body", _shipObject.AddComponent<Rigidbody2D>());
            RuntimeAssembly.SetField(_ship, "Stage", _stage);
            RuntimeAssembly.SetField(_ship, "ShipType", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), "Honeybee"));
            RuntimeAssembly.SetField(_ship, "IsUserControlled", true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_configDataType != null)
            {
                RuntimeAssembly.SetStaticField(
                    _configDataType, "Configuration", _originalConfiguration);
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
        [Category("BeesPlayModeFoundation")]
        public IEnumerator RepeatedKillReleaseReuseAndResetReturnsToBaseline()
        {
            yield return RunLifecycleCycles(CycleCount);
        }

        [UnityTest]
        [Category("BeesSoakQualification")]
        public IEnumerator ExtendedThousandCycleKillReleaseReuseAndResetReturnsToBaseline()
        {
            yield return RunLifecycleCycles(ExtendedCycleCount);
        }

        [Test]
        [Category("BeesPlayModeFoundation")]
        public void LivingShooterProjectilePastRangeIsReleasedAndUnregistered()
        {
            GameObject projectileObject = new GameObject(
                nameof(LivingShooterProjectilePastRangeIsReleasedAndUnregistered));

            try
            {
                object projectile = projectileObject.AddComponent(
                    RuntimeAssembly.GetType("Assets.Scripts.Entities.Projectiles.Projectile"));
                ((Behaviour)projectile).enabled = false;

                object projectileType = Enum.Parse(
                    RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ProjectileTypes"),
                    "BeeSmall");

                RuntimeAssembly.SetField(projectile, "Transform", projectileObject.transform);
                RuntimeAssembly.SetField(projectile, "Stage", _stage);
                RuntimeAssembly.SetField(projectile, "Level", _level);
                RuntimeAssembly.SetField(projectile, "Shooter", _ship);
                RuntimeAssembly.SetField(projectile, "StartingPosition", Vector2.zero);
                RuntimeAssembly.SetField(projectile, "Range", 10);
                RuntimeAssembly.SetField(projectile, "Type", projectileType);
                RuntimeAssembly.SetField(projectile, "IsDead", false);
                RuntimeAssembly.SetField(projectile, "ShipIsDead", false);

                RuntimeAssembly.Invoke(_state, "AddProjectile", projectile);
                RuntimeAssembly.AddToCollection(
                    RuntimeAssembly.GetField(_ship, "ProjectilesInFlight"), projectile);

                projectileObject.transform.localPosition = new Vector2(9f, 0f);
                RuntimeAssembly.Invoke(projectile, "FixedUpdate");

                Assert.That(RuntimeAssembly.GetField(projectile, "IsDead"), Is.False,
                    "A projectile still inside its weapon range was retired early.");
                Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Projectiles")),
                    Is.EqualTo(1));
                Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_ship, "ProjectilesInFlight")),
                    Is.EqualTo(1));

                projectileObject.transform.localPosition = new Vector2(11f, 0f);
                RuntimeAssembly.Invoke(projectile, "FixedUpdate");

                Assert.That(RuntimeAssembly.GetField(projectile, "IsDead"), Is.True,
                    "A missed projectile from a living shooter remained alive after exceeding weapon range.");
                Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Projectiles")),
                    Is.Zero, "Expired projectile remained registered in the owning GameState.");
                Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_ship, "ProjectilesInFlight")),
                    Is.Zero, "Expired projectile remained retained by its shooter.");

                object projectilePool = RuntimeAssembly.GetField(_pool, "BeeSmallProjectilePool");
                Assert.That(GetPoolCount(projectilePool, "CountInactive"), Is.EqualTo(1),
                    "Expired projectile was not returned to its pool.");

                object reacquiredProjectile = RuntimeAssembly.Invoke(
                    _pool, "GetProjectileFromPool", projectileType);
                Assert.That(reacquiredProjectile, Is.SameAs(projectile),
                    "Projectile pool did not reuse the expired projectile.");
                Assert.That(GetPoolCount(projectilePool, "CountInactive"), Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(projectileObject);
            }
        }

        private IEnumerator RunLifecycleCycles(int cycleCount)
        {
            object firstShip = _ship;
            object firstSquad = null;
            object honeybeePool = RuntimeAssembly.GetField(_pool, "HoneybeePool");

            for (int cycle = 0; cycle < cycleCount; cycle++)
            {
                if (cycle > 0)
                {
                    _ship = RuntimeAssembly.Invoke(honeybeePool, "Get");
                    Assert.That(_ship, Is.SameAs(firstShip),
                        $"Ship identity changed during cycle {cycle}.");
                }

                object squad = RuntimeAssembly.Invoke(_pool, "GetSquadFromPool");
                if (firstSquad == null)
                {
                    firstSquad = squad;
                }
                else
                {
                    Assert.That(squad, Is.SameAs(firstSquad),
                        $"Squad identity changed during cycle {cycle}.");
                }

                PrepareSquad(squad, cycle);
                PrepareShip(squad, cycle);
                PopulatePerCycleState(cycle);

                RuntimeAssembly.Invoke(_ship, "Kill", null, null, null, true);

                AssertKilledAndQueued(cycle, squad);
                RuntimeAssembly.Invoke(_state, "Release");

                Assert.That(GetPoolCount(honeybeePool, "CountInactive"), Is.EqualTo(1),
                    $"Honeybee pool did not return to baseline during cycle {cycle}.");
                Assert.That(GetPoolCount(RuntimeAssembly.GetField(_pool, "SquadPool"), "CountInactive"), Is.EqualTo(1),
                    $"Squad pool did not return to baseline during cycle {cycle}.");

                if (cycle + 1 < cycleCount)
                {
                    RuntimeAssembly.AddToCollection(
                        RuntimeAssembly.GetField(_ship, "ProjectilesInFlight"), null);
                    RuntimeAssembly.SetField(_ship, "PathfindingRequestId", 8000 + cycle);
                    RuntimeAssembly.SetField(_ship, "PathfindingCompletedRequestId", 8000 + cycle);
                    RuntimeAssembly.SetField(_ship, "PathfindingThreadComplete", true);
                }

                RuntimeAssembly.Invoke(_level, "ResetRuntimeState", _globalHandledRequests);
                AssertCleanBaseline(cycle);
                Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "_socket"), Is.Null,
                    $"Lifecycle cycle {cycle} unexpectedly created the global game socket.");

                yield return null;
            }
        }

        private void PrepareSquad(object squad, int cycle)
        {
            RuntimeAssembly.Invoke(squad, "ClearData");
            object savedSquad = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(squad, "SavedSquad", savedSquad);
            RuntimeAssembly.SetField(squad, "Side", 1);
            RuntimeAssembly.SetField(squad, "Level", _level);
            RuntimeAssembly.SetField(squad, "Stage", _stage);
            RuntimeAssembly.SetField(squad, "IsDead", false);
            RuntimeAssembly.SetField(squad, "Id", (long)(1000 + cycle));
            RuntimeAssembly.Invoke(_state, "AddSquad", squad);
            RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(_level, "AllSquads"), savedSquad);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(RuntimeAssembly.GetField(_level, "CurrentLevelOptions"), "ChosenSquads"),
                savedSquad);
        }

        private void PrepareShip(object squad, int cycle)
        {
            int previousLifecycleId = (int)RuntimeAssembly.GetField(
                _ship, "PathfindingLifecycleId");
            RuntimeAssembly.Invoke(_ship, "ClearData");
            Assert.That(RuntimeAssembly.GetField(_ship, "IsDead"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_ship, "IsPathfinding"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingLifecycleId"),
                Is.EqualTo(unchecked(previousLifecycleId + 1)));
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingRequestId"), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingCompletedRequestId"), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingThreadComplete"), Is.False);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_ship, "DestinationQueue")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_ship, "ProjectilesInFlight")), Is.Zero);

            object fleetShip = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Data.FleetShip");
            RuntimeAssembly.SetField(_ship, "Id", (long)(2000 + cycle));
            RuntimeAssembly.SetField(_ship, "Side", 1);
            RuntimeAssembly.SetField(_ship, "FleetShip", fleetShip);
            RuntimeAssembly.SetField(_ship, "Squad", squad);
            RuntimeAssembly.SetField(_ship, "Level", _level);
            RuntimeAssembly.SetField(_ship, "Stage", _stage);
            RuntimeAssembly.SetField(_ship, "IsUserControlled", true);
            RuntimeAssembly.SetField(_ship, "IsDead", false);
            RuntimeAssembly.AddToCollection(RuntimeAssembly.Invoke(squad, "GetShips"), _ship);
            RuntimeAssembly.Invoke(_state, "AddShip", _ship);
        }

        private void PopulatePerCycleState(int cycle)
        {
            RuntimeAssembly.SetField(_ship, "IsPathfinding", true);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(_ship, "DestinationQueue"), new Vector2(cycle + 1, cycle + 2));

            object timer = Activator.CreateInstance(RuntimeAssembly.GetType("Assets.Scripts.ScaledTimer"));
            RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(_level, "Timers"), timer);
            RuntimeAssembly.SetField(_level, "Seconds", 10f + cycle);
            RuntimeAssembly.SetField(_level, "_hasSetTimeoutTimer", true);

            long ownedHash = 3000L + cycle;
            ((ISet<long>)RuntimeAssembly.GetField(_level, "HandledRequests")).Add(ownedHash);
            _globalHandledRequests.Add(ownedHash);
            _globalHandledRequests.Add(999999L);

            RuntimeAssembly.SetField(_state, "GameOver", true);
            RuntimeAssembly.SetField(_state, "LevelEnded", true);
            RuntimeAssembly.SetField(_state, "IsPaused", true);
        }

        private void AssertKilledAndQueued(int cycle, object squad)
        {
            Assert.That(RuntimeAssembly.GetField(_ship, "IsDead"), Is.True,
                $"Ship remained alive during cycle {cycle}.");
            Assert.That(RuntimeAssembly.GetField(squad, "IsDead"), Is.True,
                $"Empty squad remained alive during cycle {cycle}.");
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Squads")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "SquadsToRelease")), Is.EqualTo(1));
        }

        private void AssertCleanBaseline(int cycle)
        {
            string suffix = $" after cycle {cycle}.";
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.Zero, "Ships leaked" + suffix);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Squads")), Is.Zero, "Squads leaked" + suffix);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.Zero, "Ship releases leaked" + suffix);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "SquadsToRelease")), Is.Zero, "Squad releases leaked" + suffix);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_level, "Timers")), Is.Zero, "Timers leaked" + suffix);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_level, "HandledRequests")), Is.Zero, "Level request hashes leaked" + suffix);
            Assert.That(_globalHandledRequests, Is.EquivalentTo(new[] { 999999L }), "Global request hashes were corrupted" + suffix);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_level, "AllSquads")), Is.Zero, "Persistent squad references leaked" + suffix);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(
                RuntimeAssembly.GetField(_level, "CurrentLevelOptions"), "ChosenSquads")), Is.Zero, "Chosen squads leaked" + suffix);
            Assert.That(RuntimeAssembly.GetField(_level, "Seconds"), Is.EqualTo(0f), "Level time leaked" + suffix);
            Assert.That(RuntimeAssembly.GetField(_state, "GameOver"), Is.False, "GameOver leaked" + suffix);
            Assert.That(RuntimeAssembly.GetField(_state, "LevelEnded"), Is.False, "LevelEnded leaked" + suffix);
            Assert.That(RuntimeAssembly.GetField(_state, "IsPaused"), Is.False, "Pause state leaked" + suffix);
        }

        private static int GetPoolCount(object pool, string propertyName)
        {
            return (int)pool.GetType().GetProperty(propertyName).GetValue(pool);
        }
    }
}
