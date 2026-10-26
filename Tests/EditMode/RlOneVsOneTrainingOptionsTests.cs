using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneTrainingOptionsTests
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
        public void NoCommandLineOverridesPreserveExistingTrainingDefaults()
        {
            object options = Parse();

            Assert.That(GetProperty(options, "HealthRatio"), Is.EqualTo(0.25f));
            Assert.That(GetProperty(options, "MapSize"), Is.EqualTo(30f));
            Assert.That(GetProperty(options, "EpisodeTimeoutSeconds"), Is.EqualTo(120));
            Assert.That(GetProperty(options, "ShipsPerSide"), Is.EqualTo(1));
            Assert.That(GetProperty(options, "CollisionAsteroidSpawnSeconds"), Is.EqualTo(0f));
            Assert.That(GetProperty(options, "StaticObstaclesEnabled"), Is.False);
            Assert.That(GetProperty(options, "MiningAsteroidsEnabled"), Is.False);
            CollectionAssert.AreEqual(new[] { "Wasp" }, GetShipTypeNames(options, "BeeShipTypes"));
            CollectionAssert.AreEqual(new[] { "Gunship" }, GetShipTypeNames(options, "HumanShipTypes"));
        }

        [Test]
        public void CommandLineOverridesAllRequestedTrainingDimensions()
        {
            object options = Parse(
                "--rl-health-ratio=.50",
                "--rl-map-size", "60",
                "--rl-episode-timeout=45",
                "--rl-ships-per-side", "2",
                "--rl-collision-asteroid-spawn-seconds=15",
                "--rl-static-obstacles",
                "--rl-mining-asteroids=true",
                "--rl-bee-ship-types", "Wasp,Hornet",
                "--rl-human-ship-types=Gunship,Frigate");

            Assert.That(GetProperty(options, "HealthRatio"), Is.EqualTo(0.5f));
            Assert.That(GetProperty(options, "MapSize"), Is.EqualTo(60f));
            Assert.That(GetProperty(options, "EpisodeTimeoutSeconds"), Is.EqualTo(45));
            Assert.That(GetProperty(options, "ShipsPerSide"), Is.EqualTo(2));
            Assert.That(GetProperty(options, "CollisionAsteroidSpawnSeconds"), Is.EqualTo(15f));
            Assert.That(GetProperty(options, "StaticObstaclesEnabled"), Is.True);
            Assert.That(GetProperty(options, "MiningAsteroidsEnabled"), Is.True);
            CollectionAssert.AreEqual(new[] { "Wasp", "Hornet" }, GetShipTypeNames(options, "BeeShipTypes"));
            CollectionAssert.AreEqual(new[] { "Gunship", "Frigate" }, GetShipTypeNames(options, "HumanShipTypes"));
        }

        [Test]
        public void EnvironmentBooleansAcceptExplicitFalseAndCollisionZeroMeansOff()
        {
            object options = Parse(
                "--rl-collision-asteroid-spawn-seconds", "0",
                "--rl-static-obstacles=false",
                "--rl-mining-asteroids", "off");

            Assert.That(GetProperty(options, "CollisionAsteroidSpawnSeconds"), Is.EqualTo(0f));
            Assert.That(GetProperty(options, "StaticObstaclesEnabled"), Is.False);
            Assert.That(GetProperty(options, "MiningAsteroidsEnabled"), Is.False);
        }

        [Test]
        public void EpisodeLogCategoriesCanBeControlledIndependently()
        {
            Assert.That(Convert.ToInt32(GetProperty(Parse(), "EpisodeLogCategories")), Is.EqualTo(127),
                "Existing runs must retain the complete episode record by default.");

            object minimal = Parse(
                "--rl-log-combat=false",
                "--rl-log-aim=false",
                "--rl-log-aim-directions=false",
                "--rl-log-visibility=false",
                "--rl-log-environment=false",
                "--rl-log-rewards=false",
                "--rl-log-policy=false");
            Assert.That(Convert.ToInt32(GetProperty(minimal, "EpisodeLogCategories")), Is.Zero);

            object selective = Parse(
                "--rl-log-combat=false",
                "--rl-log-aim=false",
                "--rl-log-aim-directions=false",
                "--rl-log-visibility=false",
                "--rl-log-environment=false",
                "--rl-log-rewards=false",
                "--rl-log-policy=false",
                "--rl-log-combat", // bare flag turns combat back on
                "--rl-log-aim=true");
            Assert.That(Convert.ToInt32(GetProperty(selective, "EpisodeLogCategories")), Is.EqualTo(3));
            AssertParseFails("--rl-log-aim-directions=maybe");
        }

        [Test]
        public void EpisodeLogFilteringKeepsTheOutcomeWhenAllOptionalGroupsAreDisabled()
        {
            Type writer = RuntimeAssembly.GetType("RlEpisodeRecordWriter");
            Type category = RuntimeAssembly.GetType("RlEpisodeLogCategory");
            MethodInfo filter = writer.GetMethod("FilterCategories", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(filter, Is.Not.Null);

            var aimDecisions = new Dictionary<string, object>
            {
                ["team_id"] = 0,
                ["requested_samples"] = 12,
                ["target_requested_sector_counts"] = new long[8][]
            };
            var bee = new Dictionary<string, object>
            {
                ["ships"] = new[] { "Hornet" },
                ["combat"] = new Dictionary<string, object> { ["shots"] = 3 },
                ["damage_by_source"] = new Dictionary<string, object>(),
                ["border_contacts"] = 0,
                ["visibility"] = new Dictionary<string, object>(),
                ["rewards"] = new Dictionary<string, object>(),
                ["retained_mining_tsv"] = 0,
                ["aim"] = new Dictionary<string, object>
                {
                    ["samples"] = 8,
                    ["policy_decisions"] = aimDecisions,
                    ["stationary_decisions"] = new Dictionary<string, object>
                    {
                        ["requested_samples"] = 12
                    }
                }
            };
            var episode = new Dictionary<string, object>
            {
                ["schema"] = 1,
                ["episode"] = 123,
                ["outcome"] = "human",
                ["final_elimination"] = new Dictionary<string, object> { ["bee"] = "enemy" },
                ["environment"] = new Dictionary<string, object>(),
                ["bee"] = bee
            };
            // Disable only the large sector histograms/matrices. Accuracy and role
            // metadata must remain available for ordinary learning analysis.
            filter.Invoke(null, new object[] { episode, Enum.ToObject(category, 127 & ~4) });
            Assert.That(aimDecisions.ContainsKey("target_requested_sector_counts"), Is.False);
            Assert.That(aimDecisions["requested_samples"], Is.EqualTo(12));
            Assert.That(aimDecisions["team_id"], Is.EqualTo(0));

            filter.Invoke(null, new object[] { episode, Enum.Parse(category, "None") });
            CollectionAssert.AreEquivalent(
                new[] { "ships" }, bee.Keys);
            Assert.That(episode.ContainsKey("environment"), Is.False);
            Assert.That(episode["outcome"], Is.EqualTo("human"));
            Assert.That(episode.ContainsKey("final_elimination"), Is.True);
        }

        [Test]
        public void OneConfiguredTypeCanBeRepeatedAcrossAConfiguredTeamSize()
        {
            object options = Parse(
                "--rl-ships-per-side", "4",
                "--rl-bee-ship-types", "yellow-jacket",
                "--rl-human-ship-types", "gun_ship");

            Assert.That(GetProperty(options, "ShipsPerSide"), Is.EqualTo(4));
            CollectionAssert.AreEqual(new[] { "YellowJacket" }, GetShipTypeNames(options, "BeeShipTypes"));
            CollectionAssert.AreEqual(new[] { "Gunship" }, GetShipTypeNames(options, "HumanShipTypes"));
        }

        [Test]
        public void ShipsPerSideHasNoArtificialUpperBound()
        {
            object options = Parse(
                "--rl-ships-per-side", "128",
                "--rl-bee-ship-types", "Wasp",
                "--rl-human-ship-types", "Gunship");

            Assert.That(GetProperty(options, "ShipsPerSide"), Is.EqualTo(128));
        }

        [Test]
        public void SampledModeRequiresLoadedConfigurationSettings()
        {
            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object previousConfiguration = RuntimeAssembly.GetStaticField(configDataType, "Configuration");

            try
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", null);
                TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                    Parse("--rl-matchup-mode=sampled"));

                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            }
            finally
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", previousConfiguration);
            }
        }

        [Test]
        public void ValidationOnlyControlFlagUsesTheSameAuthoritativeParser()
        {
            object options = Parse(
                "--rl-validate-options-only",
                "--rl-health-ratio=.5",
                "--rl-map-size=60",
                "--rl-ships-per-side=2",
                "--rl-bee-ship-types=Wasp,Hornet",
                "--rl-human-ship-types=Gunship,Frigate");

            Assert.That(GetProperty(options, "HealthRatio"), Is.EqualTo(0.5f));
            Assert.That(GetProperty(options, "MapSize"), Is.EqualTo(60f));
            Assert.That(GetProperty(options, "ShipsPerSide"), Is.EqualTo(2));
            AssertParseFails("--rl-validate-options-only", "--rl-typo=1");
        }

        [Test]
        public void MapValidationRejectsLargeShipsOnlyWhenConfiguredMaximumCannotFitThem()
        {
            AssertParseFails(
                "--rl-map-size=48",
                "--rl-bee-ship-types=Beehive");

            object options = Parse(
                "--rl-map-size-min=48",
                "--rl-map-size-max=64",
                "--rl-bee-ship-types=Beehive");

            Assert.That(GetProperty(options, "MapSizeMinimum"), Is.EqualTo(48f));
            Assert.That(GetProperty(options, "MapSizeMaximum"), Is.EqualTo(64f));
        }

        [Test]
        public void InvalidOrAmbiguousRlOptionsFailInsteadOfSilentlyUsingDefaults()
        {
            AssertParseFails("--rl-health-ratio", "1.5");
            AssertParseFails("--rl-map-size", "5");
            AssertParseFails("--rl-episode-timeout", "0");
            AssertParseFails("--rl-ships-per-side", "0");
            AssertParseFails("--rl-collision-asteroid-spawn-seconds", "-1");
            AssertParseFails("--rl-static-obstacles=maybe");
            AssertParseFails("--rl-mining-asteroids", "maybe");
            AssertParseFails("--rl-bee-ship-types", "NotAShip");
            AssertParseFails("--rl-matchup-mode=2");
            AssertParseFails("--rl-ships-per-side", "3", "--rl-bee-ship-types", "Wasp,Hornet");
            AssertParseFails("--rl-typo", "1");
        }

        private object Parse(params string[] args)
        {
            return _parse.Invoke(null, new object[] { args });
        }

        private object GetProperty(object instance, string propertyName)
        {
            PropertyInfo property = _optionsType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null, propertyName);
            return property.GetValue(instance);
        }

        private string[] GetShipTypeNames(object instance, string propertyName)
        {
            IEnumerable values = GetProperty(instance, propertyName) as IEnumerable;
            Assert.That(values, Is.Not.Null, propertyName);

            List<string> names = new List<string>();
            foreach (object value in values)
            {
                names.Add(value.ToString());
            }
            return names.ToArray();
        }

        private void AssertParseFails(params string[] args)
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => Parse(args));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

    }
}
