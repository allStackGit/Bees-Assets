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
        public void MapSizeRangeSamplesOneStableEpisodeValueWithinInclusiveBounds()
        {
            object options = Parse(
                "--rl-map-size-min", "64",
                "--rl-map-size-max", "128");

            Assert.That(GetProperty(options, "HasMapSizeRange"), Is.EqualTo(true));
            Assert.That((float)GetProperty(options, "MapSizeMinimum"), Is.EqualTo(64f));
            Assert.That((float)GetProperty(options, "MapSizeMaximum"), Is.EqualTo(128f));

            float first = (float)GetProperty(options, "MapSize");
            float second = (float)GetProperty(options, "MapSize");
            Assert.That(first, Is.InRange(64f, 128f));
            Assert.That(second, Is.EqualTo(first),
                "The sampled map size must remain fixed for the duration of an episode.");
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
            Assert.That(telemetry, Does.Contain("RecordShotFired"));
            Assert.That(telemetry, Does.Contain("RecordHit"));
            Assert.That(telemetry, Does.Not.Contain("MonoBehaviour"));
            Assert.That(telemetry, Does.Not.Contain("private void Update()"));
            Assert.That(telemetry, Does.Not.Contain("Debug.Log("),
                "Combat telemetry must be appended to the existing episode line rather than emitting a second line.");
            Assert.That(telemetry, Does.Not.Contain("AddReward("));
            Assert.That(telemetry, Does.Not.Contain("SetReward("));

            Assert.That(diagnostics, Does.Contain("RlOneVsOneCombatTelemetry.BuildEpisodeFields()"));
            Assert.That(diagnostics, Does.Contain("RlOneVsOneCombatTelemetry.RecordHit(sourceShip, target, appliedDamage)"));
            Assert.That(CountOccurrences(turret, "RlOneVsOneCombatTelemetry.RecordShotFired(Ship, this);"), Is.EqualTo(1));
            Assert.That(CountOccurrences(beamCannon, "RlOneVsOneCombatTelemetry.RecordShotFired(Ship, this);"), Is.EqualTo(1));
            Assert.That(CountOccurrences(dualCannon, "RlOneVsOneCombatTelemetry.RecordShotFired(Ship, this);"), Is.EqualTo(2));
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
            Assert.That(config, Does.Contain("window: 20"));
            Assert.That(config, Does.Contain("play_against_latest_model_ratio: 0.25"));
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
