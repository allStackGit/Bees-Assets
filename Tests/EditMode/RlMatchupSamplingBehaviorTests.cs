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
    public class RlMatchupSamplingBehaviorTests
    {
        private Type _optionsType;
        private Type _samplerType;
        private Type _selectorType;
        private Type _compositionSamplerType;
        private Type _configDataType;
        private object _previousConfiguration;

        [SetUp]
        public void SetUp()
        {
            _optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _samplerType = RuntimeAssembly.GetType("RlOneVsOneMatchupSampler");
            _selectorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchupSelector");
            _compositionSamplerType = RuntimeAssembly.GetType("RlShipCompositionSampler");
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _previousConfiguration = RuntimeAssembly.GetStaticField(_configDataType, "Configuration");

            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            IDictionary sideMap = (IDictionary)RuntimeAssembly.GetStaticField(
                RuntimeAssembly.GetType("Assets.Scripts.Utilities"),
                "ConvertShipTypeToSide");
            int beeSide = (int)sideMap[Enum.Parse(shipType, "Wasp")];
            int humanSide = (int)sideMap[Enum.Parse(shipType, "Gunship")];

            object configuration = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(configuration, "IsLoaded", true);
            RuntimeAssembly.SetField(configuration, "BeeSide", beeSide);
            RuntimeAssembly.SetField(configuration, "HumanSide", humanSide);
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", configuration);
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", _previousConfiguration);
        }

        [Test]
        public void SampledOneVsOneSelectorKeepsExactSeededCartesianSamplerSequenceForArmedPools()
        {
            const int seed = 424242;
            object options = Parse(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", "1",
                "--rl-bee-ship-types", "Wasp,Hornet",
                "--rl-human-ship-types", "Gunship,Frigate");

            object beeTypes = GetProperty(options, "BeeShipTypes");
            object humanTypes = GetProperty(options, "HumanShipTypes");
            object sampler = CreateSampler(beeTypes, humanTypes, seed);
            object selector = CreateSelector(options, seed);
            MethodInfo next = _samplerType.GetMethod("Next", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo prepare = _selectorType.GetMethod("PrepareEpisode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(next, Is.Not.Null);
            Assert.That(prepare, Is.Not.Null);

            for (int episode = 0; episode < 8; episode++)
            {
                object expected = next.Invoke(sampler, null);
                prepare.Invoke(selector, null);
                object actual = RuntimeAssembly.GetField(selector, "_currentMatchup");

                Assert.That(RuntimeAssembly.GetField(actual, "BeeShipType").ToString(),
                    Is.EqualTo(RuntimeAssembly.GetField(expected, "BeeShipType").ToString()));
                Assert.That(RuntimeAssembly.GetField(actual, "HumanShipType").ToString(),
                    Is.EqualTo(RuntimeAssembly.GetField(expected, "HumanShipType").ToString()));
            }
        }

        [Test]
        public void MultiShipRankSpaceContainsEachUnorderedCompositionExactlyOnce()
        {
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            Array candidates = Array.CreateInstance(shipType, 3);
            candidates.SetValue(Enum.Parse(shipType, "Wasp"), 0);
            candidates.SetValue(Enum.Parse(shipType, "Hornet"), 1);
            candidates.SetValue(Enum.Parse(shipType, "YellowJacket"), 2);

            int beeSide = (int)RuntimeAssembly.InvokeStatic(
                _samplerType,
                "GetSideForShipType",
                Enum.Parse(shipType, "Wasp"));
            object sampler = CreateCompositionSampler(candidates, 2, beeSide, 1001);

            Assert.That(GetProperty(sampler, "CombinationCount"), Is.EqualTo(6L));

            HashSet<string> compositions = new HashSet<string>();
            MethodInfo createForRank = _compositionSamplerType.GetMethod(
                "CreateCompositionForRank",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(createForRank, Is.Not.Null);
            for (long rank = 0; rank < 6; rank++)
            {
                compositions.Add(Canonicalize((Array)createForRank.Invoke(sampler, new object[] { rank })));
            }

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "Hornet,Hornet",
                    "Hornet,Wasp",
                    "Hornet,YellowJacket",
                    "Wasp,Wasp",
                    "Wasp,YellowJacket",
                    "YellowJacket,YellowJacket",
                },
                compositions);
        }

        [Test]
        public void UniformCompositionSamplerNeverReturnsAnAllWeaponlessSide()
        {
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            Array candidates = Array.CreateInstance(shipType, 3);
            candidates.SetValue(Enum.Parse(shipType, "Wasp"), 0);
            candidates.SetValue(Enum.Parse(shipType, "Honeybee"), 1);
            candidates.SetValue(Enum.Parse(shipType, "CarpenterBee"), 2);

            int beeSide = (int)RuntimeAssembly.InvokeStatic(
                _samplerType,
                "GetSideForShipType",
                Enum.Parse(shipType, "Wasp"));
            object sampler = CreateCompositionSampler(candidates, 3, beeSide, 8080);
            MethodInfo next = _compositionSamplerType.GetMethod("Next", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(next, Is.Not.Null);

            HashSet<string> observed = new HashSet<string>();
            for (int episode = 0; episode < 200; episode++)
            {
                Array composition = (Array)next.Invoke(sampler, null);
                string canonical = Canonicalize(composition);
                observed.Add(canonical);
                Assert.That(canonical.Split(',').Contains("Wasp"), Is.True,
                    "With Wasp as the only armed candidate, every accepted composition must contain a Wasp.");
            }

            Assert.That(observed.Count, Is.GreaterThan(1), "Sampling should still retain mixed utility/armed fleets.");
        }

        [Test]
        public void MultiShipCompositionStreamsRemainDeterministicForIdenticalSeeds()
        {
            const int seed = 5150;
            object options = Parse(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", "4",
                "--rl-bee-ship-types", "Wasp,Hornet,YellowJacket",
                "--rl-human-ship-types", "Gunship,Frigate,Cruiser");
            object first = CreateSelector(options, seed);
            object second = CreateSelector(options, seed);

            for (int episode = 0; episode < 32; episode++)
            {
                RuntimeAssembly.Invoke(first, "PrepareEpisode");
                RuntimeAssembly.Invoke(second, "PrepareEpisode");

                Assert.That(
                    Canonicalize((Array)RuntimeAssembly.GetField(first, "_currentBeeComposition")),
                    Is.EqualTo(Canonicalize((Array)RuntimeAssembly.GetField(second, "_currentBeeComposition"))));
                Assert.That(
                    Canonicalize((Array)RuntimeAssembly.GetField(first, "_currentHumanComposition")),
                    Is.EqualTo(Canonicalize((Array)RuntimeAssembly.GetField(second, "_currentHumanComposition"))));
            }
        }

        private object Parse(params string[] args)
        {
            MethodInfo parse = _optionsType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(parse, Is.Not.Null);
            return parse.Invoke(null, new object[] { args });
        }

        private object CreateSelector(object options, int seed)
        {
            ConstructorInfo constructor = _selectorType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { _optionsType, typeof(int) },
                null);
            Assert.That(constructor, Is.Not.Null);
            return constructor.Invoke(new[] { options, (object)seed });
        }

        private object CreateSampler(object beeTypes, object humanTypes, int seed)
        {
            ConstructorInfo constructor = _samplerType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 3);
            return constructor.Invoke(new[] { beeTypes, humanTypes, (object)seed });
        }

        private object CreateCompositionSampler(object shipTypes, int shipsPerSide, int side, int seed)
        {
            ConstructorInfo constructor = _compositionSamplerType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 4);
            return constructor.Invoke(new[] { shipTypes, (object)shipsPerSide, (object)side, (object)seed });
        }

        private object GetProperty(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            return property.GetValue(instance);
        }

        private static string Canonicalize(Array values)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < values.Length; i++)
            {
                names.Add(values.GetValue(i).ToString());
            }
            names.Sort(StringComparer.Ordinal);
            return string.Join(",", names);
        }
    }
}
