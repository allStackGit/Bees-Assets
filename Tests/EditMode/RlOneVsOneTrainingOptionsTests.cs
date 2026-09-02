using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
                "--rl-bee-ship-types", "Wasp,Hornet",
                "--rl-human-ship-types=Gunship,Frigate");

            Assert.That(GetProperty(options, "HealthRatio"), Is.EqualTo(0.5f));
            Assert.That(GetProperty(options, "MapSize"), Is.EqualTo(60f));
            Assert.That(GetProperty(options, "EpisodeTimeoutSeconds"), Is.EqualTo(45));
            Assert.That(GetProperty(options, "ShipsPerSide"), Is.EqualTo(2));
            CollectionAssert.AreEqual(new[] { "Wasp", "Hornet" }, GetShipTypeNames(options, "BeeShipTypes"));
            CollectionAssert.AreEqual(new[] { "Gunship", "Frigate" }, GetShipTypeNames(options, "HumanShipTypes"));
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
        public void SampledModeCanParseBeforeGlobalConfigurationLoads()
        {
            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object previousConfiguration = RuntimeAssembly.GetStaticField(configDataType, "Configuration");

            try
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", null);
                object options = Parse("--rl-matchup-mode=sampled");

                Assert.That(GetProperty(options, "MatchupMode").ToString(), Is.EqualTo("Sampled"));
                CollectionAssert.Contains(GetShipTypeNames(options, "BeeShipTypes"), "Wasp");
                CollectionAssert.Contains(GetShipTypeNames(options, "HumanShipTypes"), "Gunship");
            }
            finally
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", previousConfiguration);
            }
        }

        [Test]
        public void InvalidOrAmbiguousRlOptionsFailInsteadOfSilentlyUsingDefaults()
        {
            AssertParseFails("--rl-health-ratio", "1.5");
            AssertParseFails("--rl-map-size", "5");
            AssertParseFails("--rl-episode-timeout", "0");
            AssertParseFails("--rl-ships-per-side", "17");
            AssertParseFails("--rl-bee-ship-types", "NotAShip");
            AssertParseFails("--rl-ships-per-side", "3", "--rl-bee-ship-types", "Wasp,Hornet");
            AssertParseFails("--rl-typo", "1");
        }

        [Test]
        public void RuntimeTrainingPathConsumesOptionsForDurabilityArenaTimeoutRosterAgentsAndDiagnostics()
        {
            string bootstrap = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");
            Assert.That(bootstrap, Does.Contain("stage.TimeoutTime = options.EpisodeTimeoutSeconds;"));
            Assert.That(bootstrap, Does.Contain("float mapSize = CurrentMapSize;"));
            Assert.That(bootstrap, Does.Contain("CurrentShipsPerSide => RuntimeOptions.ShipsPerSide"));
            Assert.That(bootstrap, Does.Contain("stage.OverrideBeeShipTypes = new List<ConfigData.ShipTypes>(options.BeeShipTypes);"));
            Assert.That(bootstrap, Does.Contain("stage.OverrideHumanShipTypes = new List<ConfigData.ShipTypes>(options.HumanShipTypes);"));

            string durability = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingDurabilityGuard.cs");
            Assert.That(durability, Does.Contain("RlOneVsOneTrainingBootstrap.CurrentHealthRatio"));

            string squadSetup = ReadSource("Scripts", "Levels", "Level.RandomSquadSetup.cs");
            Assert.That(squadSetup, Does.Contain("shipIndex < shipCount"));
            Assert.That(squadSetup, Does.Contain("RlOneVsOneEpisodeMatchups.PrepareEpisode()"));
            Assert.That(squadSetup, Does.Contain("RlOneVsOneEpisodeMatchups.GetShipType(side, shipIndex)"));
            Assert.That(squadSetup, Does.Contain("GetShipFormationOffset(shipIndex)"));

            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            Assert.That(agent, Does.Contain("int initialSlots = RlOneVsOneTrainingBootstrap.CurrentShipsPerSide;"));
            Assert.That(agent, Does.Contain("ProvisionAgentsForSpawnedShips(_stage);"));
            Assert.That(agent, Does.Contain("Mathf.Max(RlOneVsOneTrainingBootstrap.CurrentShipsPerSide, CountPolicyControlledShips(level, beeSide))"));
            Assert.That(agent, Does.Contain("_hasBoundShip"));
            Assert.That(agent, Does.Contain("TryBindShip()"));

            string perception = ReadSource("Scripts", "Scenes", "RlCombatPerception.cs");
            Assert.That(perception, Does.Contain("GetShipsVisibleToHiveMind(side)"));

            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            Assert.That(coordinator, Does.Contain("CaptureShotBaselines"));
            Assert.That(coordinator, Does.Contain("CalculateShotsFired"));
            Assert.That(coordinator, Does.Contain("CurrentTimeoutSeconds"));
            Assert.That(coordinator, Does.Contain("ships_per_side="));
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
