using System;
using System.IO;
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
                "--rl-map-size-min", "64",
                "--rl-map-size-max", "128");

            Assert.That(GetProperty(options, "HasMapSizeRange"), Is.EqualTo(true));
            Assert.That((float)GetProperty(options, "MapSizeMinimum"), Is.EqualTo(64f));
            Assert.That((float)GetProperty(options, "MapSizeMaximum"), Is.EqualTo(128f));
            Assert.That((float)GetProperty(options, "MapSize"), Is.EqualTo(64f),
                "Process-global callers must use the conservative minimum instead of mutable episode state.");

            Type mapStateType = RuntimeAssembly.GetType("RlOneVsOneArenaMapSizeState");
            MethodInfo sample = mapStateType.GetMethod("SampleMapSize", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(sample, Is.Not.Null);
            for (int i = 0; i < 32; i++)
            {
                float value = (float)sample.Invoke(null, new object[] { 64f, 128f });
                Assert.That(value, Is.InRange(64f, 128f));
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

        [Test]
        public void TrainingTelemetryReportsAimQualityAndFirstEngagementDistancesWithoutPerFrameScanning()
        {
            string telemetry = ReadSource("Scripts", "Scenes", "RlOneVsOneCombatTelemetry.cs");
            string diagnostics = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeDiagnostics.cs");
            string turret = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Aiming.cs");
            string beamCannon = ReadSource("Scripts", "Entities", "Ships", "Weapons", "BeamCannon.cs");
            string dualCannon = ReadSource("Scripts", "Entities", "Ships", "Weapons", "DualCannon.cs");

            Assert.That(telemetry, Does.Contain("bee_aim_error="));
            Assert.That(telemetry, Does.Contain("bee_aim_within_5deg="));
            Assert.That(telemetry, Does.Contain("bee_turret_aligned="));
            Assert.That(telemetry, Does.Contain("bee_first_fire_distance="));
            Assert.That(telemetry, Does.Contain("bee_first_hit_distance="));
            Assert.That(telemetry, Does.Contain("human_aim_error="));
            Assert.That(telemetry, Does.Contain("human_first_fire_distance="));
            Assert.That(telemetry, Does.Contain("human_first_hit_distance="));
            Assert.That(telemetry, Does.Contain("map_size="));
            Assert.That(telemetry, Does.Contain("RlOneVsOneArenaMapSizeState.GetMapSize(level)"));
            Assert.That(telemetry, Does.Contain("RecordShotFired"));
            Assert.That(telemetry, Does.Contain("RecordHit"));
            Assert.That(telemetry, Does.Contain("!turret.IsRlControlled"),
                "Aim aggregates should describe policy-controlled fire rather than automatic pre-bind shots.");
            Assert.That(telemetry, Does.Contain("turret.RlTargetPoint"),
                "Aim error should be measured from the policy's requested aim point.");
            Assert.That(telemetry, Does.Not.Contain("MonoBehaviour"));
            Assert.That(telemetry, Does.Not.Contain("private void Update()"));
            Assert.That(telemetry, Does.Not.Contain("Debug.Log("),
                "Combat telemetry must be appended to the existing episode line rather than emitting a second line.");
            Assert.That(telemetry, Does.Not.Contain("AddReward("));
            Assert.That(telemetry, Does.Not.Contain("SetReward("));

            Assert.That(diagnostics, Does.Contain("RlOneVsOneCombatTelemetry.BuildEpisodeFields(level)"));
            Assert.That(diagnostics, Does.Contain("RlOneVsOneCombatTelemetry.RecordHit(sourceShip, target, appliedDamage)"));
            Assert.That(CountOccurrences(turret, "RlOneVsOneCombatTelemetry.RecordShotFired(Ship, this);"), Is.EqualTo(1));
            Assert.That(CountOccurrences(beamCannon, "RlOneVsOneCombatTelemetry.RecordShotFired(Ship, this);"), Is.EqualTo(1));
            Assert.That(CountOccurrences(dualCannon, "RlOneVsOneCombatTelemetry.RecordShotFired(Ship, this);"), Is.EqualTo(2));
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
        public void EpisodeCoordinatorLogsDiagnosticsForEveryArena()
        {
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");

            Assert.That(coordinator, Does.Not.Contain("private bool IsPrimaryArena"));
            Assert.That(coordinator, Does.Not.Contain("if (IsPrimaryArena)"));
            Assert.That(coordinator, Does.Contain("RlOneVsOneEpisodeDiagnostics.Begin(level);"));
            Assert.That(coordinator, Does.Contain("RlOneVsOneEpisodeDiagnostics.Track(level);"));
            Assert.That(coordinator, Does.Contain("RlOneVsOneEpisodeDiagnostics.BuildEpisodeFields(level, timedOut);"));
            Assert.That(coordinator, Does.Contain("arena={GetArenaIndex()}"));
        }

        [Test]
        public void TrainerConfigUsesMoreConservativePolicyAndOpponentUpdates()
        {
            string config = ReadSource("Training", "rl_1v1_config.yaml");

            Assert.That(config, Does.Contain("learning_rate: 0.00015"));
            Assert.That(config, Does.Contain("epsilon: 0.15"));
            Assert.That(config, Does.Contain("save_steps: 20000"));
            Assert.That(config, Does.Contain("team_change: 100000"));
            Assert.That(config, Does.Contain("swap_steps: 20000"));
            Assert.That(config, Does.Contain("window: 30"));
            Assert.That(config, Does.Contain("play_against_latest_model_ratio: 0.20"));
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
