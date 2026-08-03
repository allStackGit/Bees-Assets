using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ProjectilePoolTests
    {
        private sealed class ProjectileCase
        {
            public readonly string TypeName;
            public readonly string PrefabsField;
            public readonly string AssetPath;

            public ProjectileCase(string typeName, string prefabsField, string assetPath)
            {
                TypeName = typeName;
                PrefabsField = prefabsField;
                AssetPath = assetPath;
            }
        }

        private static readonly ProjectileCase[] Cases =
        {
            new ProjectileCase("BeeSmall", "BeeSmallLaserShotPrefab", "Assets/Prefabs/Entities/Projectiles/Bee Small Laser Shot.prefab"),
            new ProjectileCase("BeeMedium", "BeeMediumLaserShotPrefab", "Assets/Prefabs/Entities/Projectiles/Bee Medium Laser Shot.prefab"),
            new ProjectileCase("BumblebeeShot", "BumblebeeShotPrefab", "Assets/Prefabs/Entities/Projectiles/Bumblebee Laser Shot.prefab"),
            new ProjectileCase("FlagshipShot", "FlagshipShotPrefab", "Assets/Prefabs/Entities/Projectiles/Flagship Laser Shot.prefab"),
            new ProjectileCase("Rocket", "RocketPrefab", "Assets/Prefabs/Entities/Projectiles/Frigate Missle Release.prefab"),
            new ProjectileCase("HumanSmall", "HumanSmallPrefab", "Assets/Prefabs/Entities/Projectiles/Human Small Laser Shot.prefab"),
            new ProjectileCase("HumanMedium", "HumanMediumPrefab", "Assets/Prefabs/Entities/Projectiles/Medium Human Laser Shot.prefab"),
            new ProjectileCase("Beam", "BeamPrefab", "Assets/Prefabs/Entities/Projectiles/Laser Beam.prefab"),
            new ProjectileCase("SplitShot", "SplitShotPrefab", "Assets/Prefabs/Entities/Projectiles/Leafcutter Laser Shot.prefab"),
            new ProjectileCase("QueenSmall", "QueenSmallPrefab", "Assets/Prefabs/Entities/Projectiles/Queen Small Laser Shot.prefab"),
            new ProjectileCase("QueenLarge", "QueenLargePrefab", "Assets/Prefabs/Entities/Projectiles/Queen Stinger Laser Shot.prefab"),
            new ProjectileCase("StrikerBomb", "StrikerBombPrefab", "Assets/Prefabs/Entities/Projectiles/Striker Bomb.prefab"),
            new ProjectileCase("RocketExplosion", "RocketExplosionPrefab", "Assets/Prefabs/Entities/Projectiles/Frigate Rocket Explosion.prefab"),
            new ProjectileCase("FireBargeExplosion", "FireBargeExplosionPrefab", "Assets/Prefabs/Entities/Projectiles/Fire Barge Explosion.prefab"),
            new ProjectileCase("FireTankExplosion", "FireTankExplosionPrefab", "Assets/Prefabs/Entities/Projectiles/Fire Tank Explosion.prefab")
        };

        private GameObject _fixtureObject;
        private GameObject _levelObject;
        private GameObject _mapObject;
        private GameObject _shooterObject;
        private GameObject _weaponObject;
        private Component _stage;
        private object _pool;
        private Component _level;
        private Component _state;
        private Component _shooter;
        private Component _weapon;
        private Type _projectileTypeEnum;
        private readonly HashSet<GameObject> _spawnedObjects = new HashSet<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _fixtureObject = new GameObject(nameof(ProjectilePoolTests));
            _stage = _fixtureObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            Component prefabs = _fixtureObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Prefabs"));
            _pool = _fixtureObject.AddComponent(
                RuntimeAssembly.GetType("Pool"));

            RuntimeAssembly.SetField(_stage, "Prefabs", prefabs);
            RuntimeAssembly.SetField(_stage, "Pool", _pool);
            RuntimeAssembly.SetField(_stage, "IsRendering", true);
            RuntimeAssembly.SetField(_stage, "IsTraining", true);
            RuntimeAssembly.SetField(_stage, "ActivateAudio", false);

            foreach (ProjectileCase projectileCase in Cases)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(projectileCase.AssetPath);
                Assert.That(prefab, Is.Not.Null, $"Missing projectile prefab: {projectileCase.AssetPath}");
                RuntimeAssembly.SetField(prefabs, projectileCase.PrefabsField, prefab);
            }

            RuntimeAssembly.Invoke(_pool, "Setup", _stage);
            _projectileTypeEnum = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ProjectileTypes");
            SetUpProjectileRuntime();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject spawnedObject in _spawnedObjects)
            {
                if (spawnedObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(spawnedObject);
                }
            }

            UnityEngine.Object.DestroyImmediate(_fixtureObject);
            UnityEngine.Object.DestroyImmediate(_levelObject);
            UnityEngine.Object.DestroyImmediate(_mapObject);
            UnityEngine.Object.DestroyImmediate(_shooterObject);
            UnityEngine.Object.DestroyImmediate(_weaponObject);
            _spawnedObjects.Clear();
        }

        [Test]
        public void EveryConfiguredProjectileRoundTripsThroughItsPoolAndIsReused()
        {
            foreach (ProjectileCase projectileCase in Cases)
            {
                object requestedType = Enum.Parse(_projectileTypeEnum, projectileCase.TypeName);
                Component first = (Component)RuntimeAssembly.Invoke(
                    _pool, "GetProjectileFromPool", requestedType);
                _spawnedObjects.Add(first.gameObject);

                Assert.That(first, Is.Not.Null, $"Pool returned null for {projectileCase.TypeName}.");
                Assert.That(RuntimeAssembly.GetField(first, "Type"), Is.EqualTo(requestedType),
                    $"{projectileCase.AssetPath} declares the wrong projectile type.");

                RuntimeAssembly.Invoke(_pool, "ReturnProjectileToPool", first);
                Component second = (Component)RuntimeAssembly.Invoke(
                    _pool, "GetProjectileFromPool", requestedType);

                Assert.That(second, Is.SameAs(first),
                    $"{projectileCase.TypeName} was not reused after a pool round trip.");
                RuntimeAssembly.Invoke(_pool, "ReturnProjectileToPool", second);
            }
        }

        [Test]
        public void EveryConfiguredProjectileSetupKillAndReuseClearsRuntimeOwnership()
        {
            foreach (ProjectileCase projectileCase in Cases)
            {
                object requestedType = Enum.Parse(_projectileTypeEnum, projectileCase.TypeName);
                Component projectile = (Component)RuntimeAssembly.Invoke(
                    _pool, "GetProjectileFromPool", requestedType);
                _spawnedObjects.Add(projectile.gameObject);

                ExerciseLifecycle(projectileCase, projectile, requestedType, 0.35f, 17, 90);

                Component reused = (Component)RuntimeAssembly.Invoke(
                    _pool, "GetProjectileFromPool", requestedType);
                Assert.That(reused, Is.SameAs(projectile),
                    $"{projectileCase.TypeName} was not reused after Kill.");
                ExerciseLifecycle(projectileCase, reused, requestedType, 0.75f, 29, 140);
            }
        }

        [Test]
        public void EveryRuntimeCommandTypeRoundTripsThroughItsPool()
        {
            Type commandTypeEnum = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+CommandTypes");
            foreach (object commandType in Enum.GetValues(commandTypeEnum))
            {
                if (Convert.ToInt32(commandType) < 3)
                {
                    continue;
                }

                Component first = (Component)RuntimeAssembly.Invoke(_pool, "GetCommandFromPool", commandType);
                Assert.That(first, Is.Not.Null, $"Pool returned null for command {commandType}.");
                RuntimeAssembly.Invoke(_pool, "ReturnCommandToPool", first);

                Component second = (Component)RuntimeAssembly.Invoke(_pool, "GetCommandFromPool", commandType);
                Assert.That(second, Is.SameAs(first), $"Command {commandType} did not round-trip.");
                RuntimeAssembly.Invoke(_pool, "ReturnCommandToPool", second);
            }
        }

        private void SetUpProjectileRuntime()
        {
            _levelObject = new GameObject(nameof(ProjectilePoolTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(_level, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            RuntimeAssembly.SetField(_state, "Stage", _stage);
            ((Behaviour)_level).enabled = false;

            _mapObject = new GameObject(nameof(ProjectilePoolTests) + " Map");
            Component map = _mapObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.UI_Components.Map"));
            RuntimeAssembly.SetField(map, "Transform", _mapObject.transform);
            RuntimeAssembly.SetField(_level, "Map", map);

            _shooterObject = new GameObject(nameof(ProjectilePoolTests) + " Shooter");
            _shooter = _shooterObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            Component squad = _shooterObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            RuntimeAssembly.SetField(squad, "SavedSquad", RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Data.SavedSquad"));
            RuntimeAssembly.SetField(_shooter, "Transform", _shooterObject.transform);
            RuntimeAssembly.SetField(_shooter, "Stage", _stage);
            RuntimeAssembly.SetField(_shooter, "Level", _level);
            RuntimeAssembly.SetField(_shooter, "Squad", squad);
            RuntimeAssembly.SetField(_shooter, "FleetShip", RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Data.FleetShip"));
            RuntimeAssembly.SetField(_shooter, "Name", "Projectile test shooter");
            RuntimeAssembly.SetField(_shooter, "IsDead", false);
            ((Behaviour)_shooter).enabled = false;

            _weaponObject = new GameObject(nameof(ProjectilePoolTests) + " BeamCannon");
            _weapon = _weaponObject.AddComponent(RuntimeAssembly.GetType(
                "Assets.Scripts.Entities.Ships.Weapons.BeamCannon"));
            RuntimeAssembly.SetField(_weapon, "Ship", _shooter);
            RuntimeAssembly.SetField(_weapon, "Level", _level);
            RuntimeAssembly.SetField(_weapon, "Stage", _stage);
            RuntimeAssembly.SetField(_weapon, "HasSoundEffect", false);
            ((Behaviour)_weapon).enabled = false;
        }

        private void ExerciseLifecycle(
            ProjectileCase projectileCase,
            Component projectile,
            object requestedType,
            float angle,
            int power,
            int range)
        {
            SeedPreviousLifeState(projectile);
            InvokeRealSetup(projectileCase, projectile, angle, range, power);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(_shooter, "ProjectilesInFlight"), projectile);
            bool ownsTimer = projectileCase.TypeName == "Rocket" ||
                projectileCase.TypeName == "StrikerBomb";
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_level, "Timers")),
                Is.EqualTo(ownsTimer ? 1 : 0),
                $"{projectileCase.TypeName} registered an unexpected timer count during Setup.");
            if (projectileCase.TypeName == "Beam")
            {
                RuntimeAssembly.SetField(_weapon, "IsFiringLaserBeam", true);
                RuntimeAssembly.SetField(_weapon, "LaserBeamTarget", _shooter);
            }

            Assert.That(RuntimeAssembly.GetField(projectile, "IsDead"), Is.False,
                $"{projectileCase.TypeName} remained dead after Setup.");
            Assert.That(((Behaviour)projectile).enabled, Is.True,
                $"{projectileCase.TypeName} was not activated by Setup.");
            Assert.That(RuntimeAssembly.GetField(projectile, "Level"), Is.SameAs(_level));
            Assert.That(RuntimeAssembly.GetField(projectile, "Shooter"), Is.SameAs(_shooter));
            Assert.That(RuntimeAssembly.GetField(projectile, "Weapon"), Is.SameAs(_weapon));
            Assert.That(RuntimeAssembly.GetField(projectile, "Power"), Is.EqualTo(power));
            Assert.That(RuntimeAssembly.GetField(projectile, "Range"), Is.EqualTo(range));
            Assert.That((float)RuntimeAssembly.GetField(projectile, "Angle"), Is.EqualTo(angle).Within(0.0001f),
                $"{projectileCase.TypeName} cleared its newly assigned angle during Setup.");
            Transform expectedParent = projectileCase.TypeName == "StrikerBomb"
                ? _shooterObject.transform
                : _mapObject.transform;
            Assert.That(projectile.transform.parent, Is.SameAs(expectedParent),
                $"{projectileCase.TypeName} was assigned to the wrong runtime parent.");
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(projectile, "ShipsToIgnore")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(projectile, "CollidingQueue")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(projectile, "CollidingObstacleQueue")), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(projectile, "ShipIsDead"), Is.False);
            Assert.That(CollectionContains(RuntimeAssembly.GetField(_state, "Projectiles"), projectile), Is.True,
                $"{projectileCase.TypeName} was not registered in GameState.");
            Assert.That(CollectionContains(
                RuntimeAssembly.GetField(_shooter, "ProjectilesInFlight"), projectile), Is.True);
            AssertDerivedStateWasCleared(projectileCase, projectile);

            if (projectileCase.TypeName == "FireBargeExplosion")
            {
                RuntimeAssembly.AddToCollection(
                    RuntimeAssembly.GetField(_state, "FireBargeExplosions"), projectile);
            }

            RuntimeAssembly.Invoke(projectile, "Kill");

            Assert.That(RuntimeAssembly.GetField(projectile, "IsDead"), Is.True);
            Assert.That(((Behaviour)projectile).enabled, Is.False);
            Assert.That(CollectionContains(RuntimeAssembly.GetField(_state, "Projectiles"), projectile), Is.False,
                $"{projectileCase.TypeName} leaked in GameState after Kill.");
            Assert.That(CollectionContains(
                RuntimeAssembly.GetField(_shooter, "ProjectilesInFlight"), projectile), Is.False,
                $"{projectileCase.TypeName} leaked in shooter bookkeeping after Kill.");
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_level, "Timers")), Is.Zero,
                $"{projectileCase.TypeName} leaked a timer after Kill.");
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "FireBargeExplosions")), Is.Zero);
            if (projectileCase.TypeName == "Beam")
            {
                Assert.That(RuntimeAssembly.GetField(_weapon, "IsFiringLaserBeam"), Is.False);
                Assert.That(RuntimeAssembly.GetField(_weapon, "LaserBeamTarget"), Is.Null);
            }
            Assert.That(GetInactiveCount(projectileCase), Is.EqualTo(1),
                $"{projectileCase.TypeName} was not returned exactly once.");

            Assert.DoesNotThrow(() => RuntimeAssembly.Invoke(projectile, "Kill"),
                $"{projectileCase.TypeName} Kill was not idempotent.");
            Assert.That(GetInactiveCount(projectileCase), Is.EqualTo(1),
                $"{projectileCase.TypeName} was returned more than once.");
        }

        private void InvokeRealSetup(
            ProjectileCase projectileCase,
            Component projectile,
            float angle,
            int range,
            int power)
        {
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship");
            Type weaponType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.Weapon");
            Type beamCannonType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.BeamCannon");
            Type[] signature;
            object[] arguments;

            if (projectileCase.TypeName == "Beam")
            {
                signature = new[] { levelType, beamCannonType, shipType, shipType, typeof(Vector2), typeof(float), typeof(int), typeof(int) };
                arguments = new object[] { _level, _weapon, _shooter, null, new Vector2(3, 4), angle, range, power };
            }
            else if (projectileCase.TypeName == "StrikerBomb")
            {
                signature = new[] { levelType, weaponType, shipType, shipType, typeof(Vector2), typeof(float), typeof(int), typeof(int), shipType };
                arguments = new object[] { _level, _weapon, _shooter, null, new Vector2(3, 4), angle, range, power, _shooter };
            }
            else
            {
                signature = new[] { levelType, weaponType, shipType, shipType, typeof(Vector2), typeof(float), typeof(int), typeof(int) };
                arguments = new object[] { _level, _weapon, _shooter, null, new Vector2(3, 4), angle, range, power };
            }

            MethodInfo setup = projectile.GetType().GetMethod(
                "Setup", BindingFlags.Instance | BindingFlags.Public, null, signature, null);
            Assert.That(setup, Is.Not.Null, $"No runtime Setup overload found for {projectileCase.TypeName}.");
            setup.Invoke(projectile, arguments);
        }

        private void SeedPreviousLifeState(Component projectile)
        {
            RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(projectile, "ShipsToIgnore"), _shooter);
            RuntimeAssembly.GetField(projectile, "CollidingQueue").GetType()
                .GetMethod("Enqueue").Invoke(RuntimeAssembly.GetField(projectile, "CollidingQueue"), new object[] { _shooter });
            RuntimeAssembly.GetField(projectile, "CollidingObstacleQueue").GetType()
                .GetMethod("Enqueue").Invoke(RuntimeAssembly.GetField(projectile, "CollidingObstacleQueue"), new object[] { null });
            RuntimeAssembly.SetField(projectile, "ShipIsDead", true);

            FieldInfo shipsHit = FindField(projectile.GetType(), "_shipsHit");
            if (shipsHit != null)
            {
                object collection = shipsHit.GetValue(projectile);
                collection.GetType().GetMethod("Add").Invoke(collection, new object[] { _shooter });
            }
            FieldInfo harmless = FindField(projectile.GetType(), "IsHarmless");
            harmless?.SetValue(projectile, true);
        }

        private void AssertDerivedStateWasCleared(ProjectileCase projectileCase, Component projectile)
        {
            FieldInfo shipsHit = FindField(projectile.GetType(), "_shipsHit");
            if (shipsHit != null)
            {
                Assert.That(RuntimeAssembly.GetCount(shipsHit.GetValue(projectile)), Is.Zero,
                    $"{projectileCase.TypeName} retained its prior hit history.");
            }
            FieldInfo harmless = FindField(projectile.GetType(), "IsHarmless");
            if (harmless != null)
            {
                Assert.That(harmless.GetValue(projectile), Is.False,
                    $"{projectileCase.TypeName} remained harmless after reuse.");
            }
        }

        private int GetInactiveCount(ProjectileCase projectileCase)
        {
            object typedPool = RuntimeAssembly.GetField(
                _pool, projectileCase.TypeName + "ProjectilePool");
            return (int)typedPool.GetType().GetProperty("CountInactive").GetValue(typedPool);
        }

        private static bool CollectionContains(object collection, object expected)
        {
            foreach (object item in (IEnumerable)collection)
            {
                if (ReferenceEquals(item, expected))
                {
                    return true;
                }
            }
            return false;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    return field;
                }
                type = type.BaseType;
            }
            return null;
        }
    }
}
