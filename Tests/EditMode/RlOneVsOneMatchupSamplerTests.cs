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
    public class RlOneVsOneMatchupSamplerTests
    {
        private Type _optionsType;
        private Type _selectorType;
        private Type _configDataType;
        private object _previousConfiguration;
        private int _beeSide;
        private int _humanSide;

        [SetUp]
        public void SetUp()
        {
            _optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _selectorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchupSelector");
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _previousConfiguration = RuntimeAssembly.GetStaticField(_configDataType, "Configuration");

            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            IDictionary sideMap = (IDictionary)RuntimeAssembly.GetStaticField(
                RuntimeAssembly.GetType("Assets.Scripts.Utilities"),
                "ConvertShipTypeToSide");
            Assert.That(sideMap, Is.Not.Null);

            _beeSide = (int)sideMap[Enum.Parse(shipType, "Wasp")];
            _humanSide = (int)sideMap[Enum.Parse(shipType, "Gunship")];

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
        public void SampledModeRejectsPoolsThatCanOnlyProduceWeaponlessSides()
        {
            TargetInvocationException beeException = Assert.Throws<TargetInvocationException>(() =>
                Parse(
                    "--rl-matchup-mode=sampled",
                    "--rl-bee-ship-types=Beehive,Honeybee,CarpenterBee",
                    "--rl-human-ship-types=Gunship"));
            TargetInvocationException humanException = Assert.Throws<TargetInvocationException>(() =>
                Parse(
                    "--rl-matchup-mode=sampled",
                    "--rl-bee-ship-types=Wasp",
                    "--rl-human-ship-types=Scout,Factory,WarpGate"));

            Assert.That(beeException.InnerException, Is.TypeOf<ArgumentException>());
            Assert.That(humanException.InnerException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void SampledOneShipCycleExcludesWeaponlessCandidatesAndCoversEveryCombatPair()
        {
            object options = Parse(
                "--rl-matchup-mode=sampled",
                "--rl-bee-ship-types=Wasp,Hornet,Honeybee",
                "--rl-human-ship-types=Gunship,Frigate,Scout");
            object selector = CreateSelector(options, 12345);
            HashSet<string> firstCycle = new HashSet<string>();

            for (int episode = 0; episode < 4; episode++)
            {
                RuntimeAssembly.Invoke(selector, "PrepareEpisode");
                firstCycle.Add(GetPreparedPair(selector));
            }

            CollectionAssert.AreEquivalent(
                new[] { "Wasp|Gunship", "Wasp|Frigate", "Hornet|Gunship", "Hornet|Frigate" },
                firstCycle);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            Assert.That(
                new HashSet<string> { "Wasp|Gunship", "Wasp|Frigate", "Hornet|Gunship", "Hornet|Frigate" },
                Does.Contain(GetPreparedPair(selector)));
        }

        [Test]
        public void PreparedMultiShipCompositionRemainsStableWithinEpisode()
        {
            object options = Parse(
                "--rl-matchup-mode=sampled",
                "--rl-ships-per-side=3",
                "--rl-bee-ship-types=Wasp,Hornet,Honeybee",
                "--rl-human-ship-types=Gunship,Frigate,Scout");
            object selector = CreateSelector(options, 9876);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            string[] preparedPairs = new string[3];
            for (int shipIndex = 0; shipIndex < preparedPairs.Length; shipIndex++)
            {
                preparedPairs[shipIndex] = GetPreparedPair(selector, shipIndex);
            }

            for (int repetition = 0; repetition < 3; repetition++)
            {
                for (int shipIndex = 0; shipIndex < preparedPairs.Length; shipIndex++)
                {
                    Assert.That(GetPreparedPair(selector, shipIndex), Is.EqualTo(preparedPairs[shipIndex]));
                }
            }
        }

        [Test]
        public void PriorityWeightBandsProduceOneToThreeTimesTotalSamplingWeight()
        {
            Assert.That(CalculatePriorityExtraWeight(0.50f), Is.EqualTo(0f));
            Assert.That(CalculatePriorityExtraWeight(0.35f), Is.EqualTo(0.5f));
            Assert.That(CalculatePriorityExtraWeight(0.65f), Is.EqualTo(0.5f));
            Assert.That(CalculatePriorityExtraWeight(0.20f), Is.EqualTo(1f));
            Assert.That(CalculatePriorityExtraWeight(0.80f), Is.EqualTo(1f));
            Assert.That(CalculatePriorityExtraWeight(0.05f), Is.EqualTo(2f));
            Assert.That(CalculatePriorityExtraWeight(0.95f), Is.EqualTo(2f));
        }

        [Test]
        public void PriorityReplayProbabilityUsesBaselinePlusExtraWeightInsteadOfFixedReplayShare()
        {
            double probability = CalculatePriorityReplayProbability(40d, 2d);

            Assert.That(probability, Is.EqualTo(2d / 42d).Within(1e-12));
        }

        [Test]
        public void PriorityReplayRepeatsAnImbalancedMatchupWithoutAdvancingBaselineCoverage()
        {
            object options = Parse(
                "--rl-matchup-mode=sampled",
                "--rl-bee-ship-types=Wasp,Hornet",
                "--rl-human-ship-types=Gunship");
            object selector = CreateSelector(options, 13579, 1000000000000d, 4, 1);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            string first = GetPreparedPair(selector);
            RuntimeAssembly.Invoke(selector, "RecordEpisodeOutcome", _beeSide, false);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            Assert.That(GetPreparedPair(selector), Is.EqualTo(first));
        }

        [Test]
        public void BalancedHistoryFallsBackToTheNextBaselineMatchup()
        {
            object options = Parse(
                "--rl-matchup-mode=sampled",
                "--rl-bee-ship-types=Wasp,Hornet",
                "--rl-human-ship-types=Gunship");
            object selector = CreateSelector(options, 24680, 1000000000000d, 4, 1);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            string first = GetPreparedPair(selector);
            RuntimeAssembly.Invoke(selector, "RecordEpisodeOutcome", 0, false);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            Assert.That(GetPreparedPair(selector), Is.Not.EqualTo(first));
        }

        [Test]
        public void RollingWindowStopsPrioritizingAFormerlyImbalancedMatchupOnceRecentResultsBalance()
        {
            object options = Parse(
                "--rl-matchup-mode=sampled",
                "--rl-bee-ship-types=Wasp,Hornet",
                "--rl-human-ship-types=Gunship");
            object selector = CreateSelector(options, 11223, 1000000000000d, 2, 1);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            string first = GetPreparedPair(selector);
            RuntimeAssembly.Invoke(selector, "RecordEpisodeOutcome", _beeSide, false);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            Assert.That(GetPreparedPair(selector), Is.EqualTo(first));
            RuntimeAssembly.Invoke(selector, "RecordEpisodeOutcome", 0, false);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            Assert.That(GetPreparedPair(selector), Is.EqualTo(first));
            RuntimeAssembly.Invoke(selector, "RecordEpisodeOutcome", 0, false);

            RuntimeAssembly.Invoke(selector, "PrepareEpisode");
            Assert.That(GetPreparedPair(selector), Is.Not.EqualTo(first));
        }

        [Test]
        public void TrainerUsesMoreExplorationAndBroaderHistoricalOpponentPool()
        {
            string yaml = ReadSource("Training", "rl_1v1_config.yaml");

            Assert.That(yaml, Does.Contain("beta: 0.008"));
            Assert.That(yaml, Does.Contain("window: 30"));
            Assert.That(yaml, Does.Contain("play_against_latest_model_ratio: 0.20"));
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

        private object CreateSelector(
            object options,
            int seed,
            double priorityWeightScale,
            int priorityOutcomeWindow,
            int priorityMinimumSamples)
        {
            ConstructorInfo constructor = _selectorType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { _optionsType, typeof(int), typeof(double), typeof(int), typeof(int) },
                null);
            Assert.That(constructor, Is.Not.Null);
            return constructor.Invoke(new object[]
            {
                options,
                seed,
                priorityWeightScale,
                priorityOutcomeWindow,
                priorityMinimumSamples
            });
        }

        private float CalculatePriorityExtraWeight(float beeScoreRate)
        {
            object value = RuntimeAssembly.InvokeStatic(_selectorType, "CalculatePriorityExtraWeight", beeScoreRate);
            return (float)value;
        }

        private double CalculatePriorityReplayProbability(double baselineWeight, double totalExtraWeight)
        {
            object value = RuntimeAssembly.InvokeStatic(
                _selectorType,
                "CalculatePriorityReplayProbability",
                baselineWeight,
                totalExtraWeight);
            return (double)value;
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
