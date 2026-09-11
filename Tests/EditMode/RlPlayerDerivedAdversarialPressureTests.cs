using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlPlayerDerivedAdversarialPressureTests
    {
        private Type _optionsType;
        private Type _pressureType;
        private Type _selectorType;
        private Type _shipType;
        private Type _configDataType;
        private object _previousConfiguration;
        private int _beeSide;
        private int _humanSide;

        [SetUp]
        public void SetUp()
        {
            _optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _pressureType = RuntimeAssembly.GetType("RlPlayerDerivedAdversarialPressure");
            _selectorType = RuntimeAssembly.GetType("RlOneVsOneAdversarialMatchupSelector");
            _shipType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _previousConfiguration = RuntimeAssembly.GetStaticField(_configDataType, "Configuration");

            IDictionary sideMap = (IDictionary)RuntimeAssembly.GetStaticField(
                RuntimeAssembly.GetType("Assets.Scripts.Utilities"),
                "ConvertShipTypeToSide");
            _beeSide = (int)sideMap[Enum.Parse(_shipType, "Wasp")];
            _humanSide = (int)sideMap[Enum.Parse(_shipType, "Gunship")];

            object configuration = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(configuration, "IsLoaded", true);
            RuntimeAssembly.SetField(configuration, "BeeSide", _beeSide);
            RuntimeAssembly.SetField(configuration, "HumanSide", _humanSide);
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", configuration);
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", _previousConfiguration);
        }

        [Test]
        public void NoPressureFlagLeavesExistingTrainingPathUntouched()
        {
            object options = ParseOptions();
            IEnumerable scenarios = ParseScenarios(options, new[] { "game.exe" });

            Assert.That(scenarios.Cast<object>().Count(), Is.EqualTo(0));
        }

        [Test]
        public void CuratedPressureRequiresSampledModeCandidatePoolAndBoundedFraction()
        {
            object options = ParseOptions(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", "1",
                "--rl-bee-ship-types", "Wasp,Hornet",
                "--rl-human-ship-types", "Gunship,Frigate");

            string valid = "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.2";
            IEnumerable scenarios = ParseScenarios(
                options,
                new[] { "game.exe", "--bees-adversarial-matchups=" + valid });
            object scenario = scenarios.Cast<object>().Single();

            Assert.That(RuntimeAssembly.GetField(scenario, "ScenarioId"), Is.EqualTo("adv-aaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.That((double)RuntimeAssembly.GetField(scenario, "TargetFraction"), Is.EqualTo(0.2d).Within(1e-12));

            AssertPressureParseFails(
                ParseOptions(),
                "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.1");
            AssertPressureParseFails(
                options,
                "adv-aaaaaaaaaaaaaaaaaaaaaaaa:YellowJacket>Gunship@0.1");
            AssertPressureParseFails(
                options,
                "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.3;" +
                "adv-bbbbbbbbbbbbbbbbbbbbbbbb:Hornet>Frigate@0.3");
        }

        [Test]
        public void MultiShipPressureRequiresExactConfiguredTeamSize()
        {
            object options = ParseOptions(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", "2",
                "--rl-bee-ship-types", "Wasp,Hornet",
                "--rl-human-ship-types", "Gunship,Frigate");

            IEnumerable scenarios = ParseScenarios(
                options,
                new[]
                {
                    "game.exe",
                    "--bees-adversarial-matchups=adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp,Hornet>Gunship,Frigate@0.15"
                });
            Assert.That(scenarios.Cast<object>().Count(), Is.EqualTo(1));

            AssertPressureParseFails(
                options,
                "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.15");
        }

        [Test]
        public void RequestedFractionProducesMeasurableFreshEpisodePressure()
        {
            const int seed = 24680;
            const int episodes = 2000;
            object options = ParseOptions(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", "1",
                "--rl-bee-ship-types", "Wasp,Hornet",
                "--rl-human-ship-types", "Gunship,Frigate");
            object scenarios = ParseScenarios(
                options,
                new[]
                {
                    "game.exe",
                    "--bees-adversarial-matchups=adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.25"
                });
            object selector = CreateSelector(options, seed, scenarios);

            int playerDerived = 0;
            for (int episode = 0; episode < episodes; episode++)
            {
                RuntimeAssembly.Invoke(selector, "PrepareEpisode");
                string tag = (string)GetProperty(selector, "CurrentPressureTag");
                if (!tag.StartsWith("player-derived:", StringComparison.Ordinal))
                {
                    continue;
                }

                playerDerived++;
                Assert.That(
                    RuntimeAssembly.Invoke(selector, "GetShipType", _beeSide, 0).ToString(),
                    Is.EqualTo("Wasp"));
                Assert.That(
                    RuntimeAssembly.Invoke(selector, "GetShipType", _humanSide, 0).ToString(),
                    Is.EqualTo("Gunship"));
            }

            double observed = (double)playerDerived / episodes;
            Assert.That(observed, Is.InRange(0.21d, 0.29d));
            Assert.That(playerDerived, Is.LessThan(episodes / 2),
                "Player-derived pressure must remain a minority of the training distribution.");
        }

        [Test]
        public void IdenticalSeedsProduceIdenticalPressureSelectionSequence()
        {
            const int seed = 12345;
            object options = ParseOptions(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", "1",
                "--rl-bee-ship-types", "Wasp,Hornet",
                "--rl-human-ship-types", "Gunship,Frigate");
            object scenarios = ParseScenarios(
                options,
                new[]
                {
                    "game.exe",
                    "--bees-adversarial-matchups=adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.2;" +
                    "adv-bbbbbbbbbbbbbbbbbbbbbbbb:Hornet>Frigate@0.1"
                });
            object first = CreateSelector(options, seed, scenarios);
            object second = CreateSelector(options, seed, scenarios);

            for (int episode = 0; episode < 128; episode++)
            {
                RuntimeAssembly.Invoke(first, "PrepareEpisode");
                RuntimeAssembly.Invoke(second, "PrepareEpisode");
                Assert.That(
                    GetProperty(first, "CurrentPressureTag"),
                    Is.EqualTo(GetProperty(second, "CurrentPressureTag")));
            }
        }

        private object ParseOptions(params string[] args)
        {
            MethodInfo parse = _optionsType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(parse, Is.Not.Null);
            return parse.Invoke(null, new object[] { args });
        }

        private IEnumerable ParseScenarios(object options, string[] args)
        {
            MethodInfo parse = _pressureType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(parse, Is.Not.Null);
            return (IEnumerable)parse.Invoke(null, new[] { args, options });
        }

        private void AssertPressureParseFails(object options, string encoded)
        {
            MethodInfo parse = _pressureType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(parse, Is.Not.Null);
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                parse.Invoke(null, new object[]
                {
                    new[] { "game.exe", "--bees-adversarial-matchups=" + encoded },
                    options
                }));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        private object CreateSelector(object options, int seed, object scenarios)
        {
            ConstructorInfo constructor = _selectorType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 3);
            return constructor.Invoke(new[] { options, (object)seed, scenarios });
        }

        private static object GetProperty(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null, propertyName);
            return property.GetValue(instance);
        }
    }
}