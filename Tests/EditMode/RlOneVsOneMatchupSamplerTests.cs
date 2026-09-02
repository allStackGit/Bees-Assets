using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneMatchupSamplerTests
    {
        private Type _optionsType;
        private Type _selectorType;
        private int _beeSide;
        private int _humanSide;

        [SetUp]
        public void SetUp()
        {
            _optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _selectorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchupSelector");

            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object configuration = RuntimeAssembly.GetStaticField(configDataType, "Configuration");
            Assert.That(configuration, Is.Not.Null, "RL matchup tests require the loaded runtime Configuration.");
            _beeSide = (int)RuntimeAssembly.GetField(configuration, "BeeSide");
            _humanSide = (int)RuntimeAssembly.GetField(configuration, "HumanSide");
        }

        [Test]
        public void FixedModeRemainsTheDefaultProofMatchup()
        {
            object options = Parse();

            Assert.That(GetProperty(options, "MatchupMode").ToString(), Is.EqualTo("Fixed"));
            Assert.That(RuntimeAssembly.Invoke(options, "GetBeeShipType", 0).ToString(), Is.EqualTo("Wasp"));
            Assert.That(RuntimeAssembly.Invoke(options, "GetHumanShipType", 0).ToString(), Is.EqualTo("Gunship"));
        }

        [Test]
        public void SampledModeDefaultsToCompletePrimaryFleetPools()
        {
            object options = Parse("--rl-matchup-mode=sampled");

            CollectionAssert.AreEquivalent(
                new[] { "Beehive", "Bumblebee", "CarpenterBee", "Honeybee", "Hornet", "Leafcutter", "Queen", "Wasp", "YellowJacket" },
                GetShipTypeNames(options, "BeeShipTypes"));
            CollectionAssert.AreEquivalent(
                new[] { "Barge", "Carrier", "Cruiser", "Dreadnought", "Factory", "FireBarge", "Flagship", "Frigate", "Gunship", "Scout", "WarpGate" },
                GetShipTypeNames(options, "HumanShipTypes"));
        }

        [Test]
        public void SampledModeRejectsCrossFactionCandidatePools()
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                Parse(
                    "--rl-matchup-mode=sampled",
                    "--rl-bee-ship-types=Gunship",
                    "--rl-human-ship-types=Scout"));

            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void SampledCycleCoversEveryCartesianPairBeforeRecycle()
        {
            object options = Parse(
                "--rl-matchup-mode=sampled",
                "--rl-bee-ship-types=Wasp,Hornet",
                "--rl-human-ship-types=Gunship,Scout");
            object selector = CreateSelector(options, 12345);
            HashSet<string> firstCycle = new HashSet<string>();

            for (int episode = 0; episode < 4; episode++)
            {
                RuntimeAssembly.Invoke(selector, "PrepareEpisode");
                firstCycle.Add(GetPreparedPair(selector));
            }

            CollectionAssert.AreEquivalent(
                new[] { "Wasp|Gunship", "Wasp|Scout", "Hornet|Gunship", "Hornet|Scout" },
                firstCycle);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            Assert.That(
                new HashSet<string> { "Wasp|Gunship", "Wasp|Scout", "Hornet|Gunship", "Hornet|Scout" },
                Does.Contain(GetPreparedPair(selector)));
        }

        [Test]
        public void PreparedMatchupRemainsStableWithinEpisode()
        {
            object options = Parse(
                "--rl-matchup-mode=sampled",
                "--rl-ships-per-side=3",
                "--rl-bee-ship-types=Wasp,Hornet",
                "--rl-human-ship-types=Gunship,Scout");
            object selector = CreateSelector(options, 9876);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            string firstPair = GetPreparedPair(selector, 0);

            for (int shipIndex = 0; shipIndex < 3; shipIndex++)
            {
                Assert.That(GetPreparedPair(selector, shipIndex), Is.EqualTo(firstPair));
                Assert.That(GetPreparedPair(selector, shipIndex), Is.EqualTo(firstPair));
            }
        }

        private object Parse(params string[] args)
        {
            return RuntimeAssembly.InvokeStatic(_optionsType, "Parse", (object)args);
        }

        private object CreateSelector(object options, int seed)
        {
            ConstructorInfo constructor = _selectorType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { _optionsType, typeof(int) },
                null);
            Assert.That(constructor, Is.Not.Null);
            return constructor.Invoke(new[] { options, (object)seed });
        }

        private string GetPreparedPair(object selector, int shipIndex = 0)
        {
            object bee = RuntimeAssembly.Invoke(selector, "GetShipType", _beeSide, shipIndex);
            object human = RuntimeAssembly.Invoke(selector, "GetShipType", _humanSide, shipIndex);
            return bee + "|" + human;
        }

        private static object GetProperty(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            return property.GetValue(instance);
        }

        private static List<string> GetShipTypeNames(object options, string propertyName)
        {
            IEnumerable values = (IEnumerable)GetProperty(options, propertyName);
            List<string> names = new List<string>();
            foreach (object value in values)
            {
                names.Add(value.ToString());
            }
            return names;
        }
    }
}
