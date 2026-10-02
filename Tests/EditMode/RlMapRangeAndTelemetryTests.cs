using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlMapRangeAndTelemetryTests
    {
        private Type _optionsType;
        private MethodInfo _parse;

        [SetUp]
        public void SetUp()
        {
            _optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _parse = _optionsType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(_parse, Is.Not.Null);
        }

        [Test]
        public void MapSizeRangeKeepsGlobalFallbackStableWhileArenaSamplerUsesConfiguredBounds()
        {
            object options = Parse(
                "--rl-map-size-min", "32",
                "--rl-map-size-max", "48");

            Assert.That(GetProperty(options, "HasMapSizeRange"), Is.EqualTo(true));
            Assert.That((float)GetProperty(options, "MapSizeMinimum"), Is.EqualTo(32f));
            Assert.That((float)GetProperty(options, "MapSizeMaximum"), Is.EqualTo(48f));
            Assert.That((float)GetProperty(options, "MapSize"), Is.EqualTo(32f),
                "Process-global callers must use the conservative minimum instead of mutable episode state.");

            Type mapStateType = RuntimeAssembly.GetType("RlOneVsOneArenaMapSizeState");
            MethodInfo sample = mapStateType.GetMethod("SampleMapSize", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(sample, Is.Not.Null);
            for (int i = 0; i < 32; i++)
            {
                float value = (float)sample.Invoke(null, new object[] { 32f, 48f });
                Assert.That(
                    value == 32f || value == 36f || value == 40f || value == 44f || value == 48f,
                    Is.True,
                    "Sampler must return only configured 4-unit map-size steps.");
                Assert.That(value, Is.EqualTo(Mathf.Round(value)));
                Assert.That((value - 32f) % 4f, Is.EqualTo(0f));
            }
        }

        [TestCase(32.4f, 32.8f)]
        [TestCase(32.6f, 32.8f)]
        public void SampledMapSizeStaysWithinDecimalBounds(float minimum, float maximum)
        {
            Type mapStateType = RuntimeAssembly.GetType("RlOneVsOneArenaMapSizeState");
            MethodInfo sample = mapStateType.GetMethod("SampleMapSize", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(sample, Is.Not.Null);

            for (int i = 0; i < 8; i++)
            {
                float value = (float)sample.Invoke(null, new object[] { minimum, maximum });
                Assert.That(value, Is.InRange(minimum, maximum));
            }
        }

        [Test]
        public void ShipFitValidationUsesTheSampledArenaSize()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            Type mapStateType = RuntimeAssembly.GetType("RlOneVsOneArenaMapSizeState");
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            MethodInfo setMapSize = mapStateType.GetMethod("SetMapSizeForTests", flags);
            MethodInfo resetMapSizes = mapStateType.GetMethod("ResetForTests", flags);
            MethodInfo fits = agentType.GetMethod("DoesShipExtentFitArena", flags);

            Assert.That(setMapSize, Is.Not.Null);
            Assert.That(resetMapSizes, Is.Not.Null);
            Assert.That(fits, Is.Not.Null);

            GameObject smallArenaObject = new GameObject("RL small sampled arena");
            GameObject largeArenaObject = new GameObject("RL large sampled arena");
            Component smallArena = smallArenaObject.AddComponent(levelType);
            Component largeArena = largeArenaObject.AddComponent(levelType);
            try
            {
                resetMapSizes.Invoke(null, null);
                setMapSize.Invoke(null, new object[] { smallArena, 32f });
                setMapSize.Invoke(null, new object[] { largeArena, 64f });

                Assert.That((bool)fits.Invoke(null, new object[] { smallArena, 20f }), Is.False,
                    "A 40-unit-diameter ship must not be accepted into a 32-unit sampled arena.");
                Assert.That((bool)fits.Invoke(null, new object[] { largeArena, 20f }), Is.True,
                    "The same ship should be accepted when that specific arena sampled 64 units.");
            }
            finally
            {
                resetMapSizes.Invoke(null, null);
                UnityEngine.Object.DestroyImmediate(smallArenaObject);
                UnityEngine.Object.DestroyImmediate(largeArenaObject);
            }
        }

        [Test]
        public void MapSizeRangeRejectsPartialOrAmbiguousConfiguration()
        {
            AssertParseFails("--rl-map-size-min", "64");
            AssertParseFails("--rl-map-size-max", "128");
            AssertParseFails(
                "--rl-map-size", "96",
                "--rl-map-size-min", "64",
                "--rl-map-size-max", "128");
            AssertParseFails(
                "--rl-map-size-min", "128",
                "--rl-map-size-max", "64");
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void ArenaMovementGuardIsDisabledWhenStaticObstaclesAreLethal(
            bool staticObstaclesEnabled,
            bool expectedConfinement)
        {
            Type bootstrapType = RuntimeAssembly.GetType("RlOneVsOneTrainingBootstrap");
            MethodInfo shouldConstrain = bootstrapType.GetMethod(
                "ShouldConstrainShipsToArena",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.That(shouldConstrain, Is.Not.Null);
            Assert.That(
                (bool)shouldConstrain.Invoke(null, new object[] { staticObstaclesEnabled }),
                Is.EqualTo(expectedConfinement));
        }

        [Test]
        public void MultiArenaTelemetryKeepsEpisodeStateIndependentPerLevel()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type diagnosticsType = RuntimeAssembly.GetType("RlOneVsOneEpisodeDiagnostics");
            Type telemetryType = RuntimeAssembly.GetType("RlOneVsOneCombatTelemetry");
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");

            MethodInfo setDiagnostics = diagnosticsType.GetMethod("SetStateForTests", flags);
            MethodInfo buildDiagnostics = diagnosticsType.GetMethod("BuildEpisodeFields", flags);
            MethodInfo endDiagnostics = diagnosticsType.GetMethod("End", flags);
            MethodInfo diagnosticsCount = diagnosticsType.GetMethod("GetTrackedLevelCountForTests", flags);
            MethodInfo resetDiagnostics = diagnosticsType.GetMethod("ResetForTests", flags);
            MethodInfo setTelemetry = telemetryType.GetMethod("SetStateForTests", flags);
            MethodInfo telemetryCount = telemetryType.GetMethod("GetTrackedLevelCountForTests", flags);
            MethodInfo resetTelemetry = telemetryType.GetMethod("ResetForTests", flags);

            Assert.That(setDiagnostics, Is.Not.Null);
            Assert.That(buildDiagnostics, Is.Not.Null);
            Assert.That(endDiagnostics, Is.Not.Null);
            Assert.That(diagnosticsCount, Is.Not.Null);
            Assert.That(resetDiagnostics, Is.Not.Null);
            Assert.That(setTelemetry, Is.Not.Null);
            Assert.That(telemetryCount, Is.Not.Null);
            Assert.That(resetTelemetry, Is.Not.Null);

            GameObject arenaAObject = new GameObject("RL telemetry arena A");
            GameObject arenaBObject = new GameObject("RL telemetry arena B");
            Component arenaA = arenaAObject.AddComponent(levelType);
            Component arenaB = arenaBObject.AddComponent(levelType);

            try
            {
                resetDiagnostics.Invoke(null, null);
                resetTelemetry.Invoke(null, null);

                setDiagnostics.Invoke(null, new object[] { arenaA, 1, 2 });
                setDiagnostics.Invoke(null, new object[] { arenaB, 1, 2 });
                setTelemetry.Invoke(null, new object[] { arenaA, 1, 2, 64f });
                setTelemetry.Invoke(null, new object[] { arenaB, 1, 2, 128f });

                Assert.That((int)diagnosticsCount.Invoke(null, null), Is.EqualTo(2));
                Assert.That((int)telemetryCount.Invoke(null, null), Is.EqualTo(2));

                string arenaAFields = (string)buildDiagnostics.Invoke(null, new object[] { arenaA, false });
                string arenaBFields = (string)buildDiagnostics.Invoke(null, new object[] { arenaB, false });
                Assert.That(arenaAFields, Does.Contain("map_size=64.00"));
                Assert.That(arenaBFields, Does.Contain("map_size=128.00"));

                endDiagnostics.Invoke(null, new object[] { arenaA });

                Assert.That((int)diagnosticsCount.Invoke(null, null), Is.EqualTo(1),
                    "Ending one arena must remove only that arena's behavior diagnostics.");
                Assert.That((int)telemetryCount.Invoke(null, null), Is.EqualTo(1),
                    "Ending one arena must remove only that arena's combat telemetry.");
                string arenaBAfter = (string)buildDiagnostics.Invoke(null, new object[] { arenaB, false });
                Assert.That(arenaBAfter, Does.Contain("map_size=128.00"),
                    "Another active arena must retain its own telemetry after a peer arena ends.");
            }
            finally
            {
                resetDiagnostics.Invoke(null, null);
                resetTelemetry.Invoke(null, null);
                UnityEngine.Object.DestroyImmediate(arenaAObject);
                UnityEngine.Object.DestroyImmediate(arenaBObject);
            }
        }

        [Test]
        public void CollisionAsteroidPresentationIsOptionalForHeadlessLifecycle()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type asteroidType = RuntimeAssembly.GetType("Assets.Scripts.Entities.CollisionAsteroid");
            MethodInfo clearData = asteroidType.GetMethod("ClearData", flags);
            MethodInfo switchToCracked = asteroidType.GetMethod("SwitchToCrackedSprite", flags);

            Assert.That(clearData, Is.Not.Null);
            Assert.That(switchToCracked, Is.Not.Null);

            GameObject asteroidObject = new GameObject("Headless collision asteroid");
            Component asteroid = asteroidObject.AddComponent(asteroidType);
            try
            {
                Assert.DoesNotThrow(() => clearData.Invoke(asteroid, null),
                    "Headless asteroid reset must not require a SpriteRenderer.");
                Assert.DoesNotThrow(() => switchToCracked.Invoke(asteroid, null),
                    "Headless asteroid damage lifecycle must not require a SpriteRenderer.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asteroidObject);
            }
        }

        [Test]
        public void EnvironmentTelemetryCountsHazardsMiningAndUniqueAsteroidsPerArena()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type diagnosticsType = RuntimeAssembly.GetType("RlOneVsOneEpisodeDiagnostics");
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship");
            Type miningAsteroidType = RuntimeAssembly.GetType("Assets.Scripts.Entities.MiningAsteroid");

            MethodInfo setState = diagnosticsType.GetMethod("SetStateForTests", flags);
            MethodInfo reset = diagnosticsType.GetMethod("ResetForTests", flags);
            MethodInfo recordDamage = diagnosticsType.GetMethod("RecordUnattributedDamage", flags);
            MethodInfo recordMining = diagnosticsType.GetMethod("RecordMiningOutcome", flags);
            MethodInfo recordAsteroidSpawn = diagnosticsType.GetMethod("RecordCollisionAsteroidSpawned", flags);
            MethodInfo buildEnvironment = diagnosticsType.GetMethod("BuildEnvironmentEpisodeFields", flags);
            MethodInfo buildDetail = diagnosticsType.GetMethod("BuildEpisodeFields", flags);

            Assert.That(setState, Is.Not.Null);
            Assert.That(reset, Is.Not.Null);
            Assert.That(recordDamage, Is.Not.Null);
            Assert.That(recordMining, Is.Not.Null);
            Assert.That(recordAsteroidSpawn, Is.Not.Null);
            Assert.That(buildEnvironment, Is.Not.Null);
            Assert.That(buildDetail, Is.Not.Null);

            GameObject arenaObject = new GameObject("RL environment telemetry arena");
            GameObject beeObject = new GameObject("RL telemetry bee ship");
            GameObject humanObject = new GameObject("RL telemetry human ship");
            GameObject borderObject = new GameObject("RL telemetry border ship");
            GameObject asteroidObject = new GameObject("RL telemetry mining asteroid");
            Component arena = arenaObject.AddComponent(levelType);
            Component beeShip = beeObject.AddComponent(shipType);
            Component humanShip = humanObject.AddComponent(shipType);
            Component borderShip = borderObject.AddComponent(shipType);
            Component asteroid = asteroidObject.AddComponent(miningAsteroidType);

            try
            {
                reset.Invoke(null, null);
                setState.Invoke(null, new object[] { arena, 1, 2 });

                SetField(beeShip, "Level", arena);
                SetField(beeShip, "Side", 1);
                SetField(beeShip, "Id", 101L);
                SetField(beeShip, "Health", 0);

                SetField(humanShip, "Level", arena);
                SetField(humanShip, "Side", 2);
                SetField(humanShip, "Id", 102L);
                SetField(humanShip, "Health", 5);

                SetField(borderShip, "Level", arena);
                SetField(borderShip, "Side", 1);
                SetField(borderShip, "Id", 103L);
                SetField(borderShip, "Health", 0);

                SetField(asteroid, "Level", arena);
                SetField(asteroid, "Id", 201);

                recordDamage.Invoke(null, new object[] { beeShip, 30, "static_obstacle", false });
                recordDamage.Invoke(null, new object[] { borderShip, 30, "map_border", false });
                recordDamage.Invoke(null, new object[] { humanShip, 5, "collision_asteroid", false });
                SetField(humanShip, "Health", 0);
                recordDamage.Invoke(null, new object[] { humanShip, 10, "collision_asteroid", false });

                recordMining.Invoke(null, new object[] { beeShip, asteroid, 12, false });
                recordMining.Invoke(null, new object[] { beeShip, asteroid, 8, true });
                recordAsteroidSpawn.Invoke(null, new object[] { arena });
                recordAsteroidSpawn.Invoke(null, new object[] { arena });

                string environment = (string)buildEnvironment.Invoke(null, new object[] { arena });
                Assert.That(environment, Does.Contain("bee_static_contacts=1"));
                Assert.That(environment, Does.Contain("bee_static_deaths=1"));
                Assert.That(environment, Does.Contain("bee_border_contacts=1"));
                Assert.That(environment, Does.Contain("bee_border_deaths=1"));
                Assert.That(environment, Does.Contain("human_asteroid_hits=2"));
                Assert.That(environment, Does.Contain("human_asteroid_damage=15"));
                Assert.That(environment, Does.Contain("human_asteroid_deaths=1"));
                Assert.That(environment, Does.Contain("collision_asteroids_spawned=2"));
                Assert.That(environment, Does.Contain("bee_mining_events=2"));
                Assert.That(environment, Does.Contain("bee_resources_mined=20"));
                Assert.That(environment, Does.Contain("bee_mining_asteroids_mined=1"),
                    "Repeated extraction from one asteroid must count that asteroid once.");
                Assert.That(environment, Does.Contain("bee_mining_asteroids_depleted=1"));

                string detail = (string)buildDetail.Invoke(null, new object[] { arena, false });
                Assert.That(detail, Does.Not.Contain("bee_static_deaths_by_ship=none"));
                Assert.That(detail, Does.Not.Contain("human_asteroid_deaths_by_ship=none"));
                Assert.That(detail, Does.Not.Contain("bee_border_deaths_by_ship=none"));
                Assert.That(detail, Does.Contain("/static_obstacle:1"),
                    "Lethal static contacts should become explicit ship outcomes.");
                Assert.That(detail, Does.Contain("/map_border:1"),
                    "Lethal map-border contacts should remain distinct from static obstacles.");
                Assert.That(detail, Does.Contain("/collision_asteroid:1"),
                    "Lethal asteroid contacts should become explicit ship outcomes.");
            }
            finally
            {
                reset.Invoke(null, null);
                UnityEngine.Object.DestroyImmediate(asteroidObject);
                UnityEngine.Object.DestroyImmediate(borderObject);
                UnityEngine.Object.DestroyImmediate(humanObject);
                UnityEngine.Object.DestroyImmediate(beeObject);
                UnityEngine.Object.DestroyImmediate(arenaObject);
            }
        }

        private object Parse(params string[] args)
        {
            return _parse.Invoke(null, new object[] { args });
        }

        private object GetProperty(object instance, string propertyName)
        {
            PropertyInfo property = _optionsType.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null, propertyName);
            return property.GetValue(instance);
        }

        private void AssertParseFails(params string[] args)
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => Parse(args));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            field.SetValue(target, value);
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

    }
}
