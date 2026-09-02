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

        [SetUp]
        public void SetUp()
        {
            _optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _samplerType = RuntimeAssembly.GetType("RlOneVsOneMatchupSampler");
            _selectorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchupSelector");
        }

        [Test]
        public void SampledOneVsOneSelectorKeepsExactSeededCartesianSamplerSequence()
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
        public void SampledMultiShipCompositionsAreDeterministicAndDoNotRepeatBeforePoolExhaustion()
        {
            const int seed = 8080;
            object options = Parse(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", "4",
                "--rl-bee-ship-types", "Wasp,Hornet,YellowJacket",
                "--rl-human-ship-types", "Gunship,Frigate");

            object first = CreateSelector(options, seed);
            object second = CreateSelector(options, seed);
            List<string> firstBee = new List<string>();
            List<string> firstHuman = new List<string>();
            List<string> secondBee = new List<string>();
            List<string> secondHuman = new List<string>();

            for (int episode = 0; episode < 6; episode++)
            {
                AppendComposition(first, firstBee, firstHuman);
                AppendComposition(second, secondBee, secondHuman);
            }

            CollectionAssert.AreEqual(firstBee, secondBee, "Identical seeds must produce identical Bee composition streams.");
            CollectionAssert.AreEqual(firstHuman, secondHuman, "Identical seeds must produce identical Human composition streams.");

            AssertEveryPoolCycleContainsAllCandidates(
                firstBee,
                new[] { "Wasp", "Hornet", "YellowJacket" });
            AssertEveryPoolCycleContainsAllCandidates(
                firstHuman,
                new[] { "Gunship", "Frigate" });
        }

        private void AppendComposition(
            object selector,
            List<string> beeDestination,
            List<string> humanDestination)
        {
            MethodInfo prepare = _selectorType.GetMethod("PrepareEpisode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(prepare, Is.Not.Null);
            prepare.Invoke(selector, null);

            AppendNames((Array)RuntimeAssembly.GetField(selector, "_currentBeeComposition"), beeDestination);
            AppendNames((Array)RuntimeAssembly.GetField(selector, "_currentHumanComposition"), humanDestination);
        }

        private static void AppendNames(Array values, List<string> destination)
        {
            Assert.That(values, Is.Not.Null);
            for (int i = 0; i < values.Length; i++)
            {
                destination.Add(values.GetValue(i).ToString());
            }
        }

        private static void AssertEveryPoolCycleContainsAllCandidates(
            List<string> stream,
            string[] candidates)
        {
            HashSet<string> expected = new HashSet<string>(candidates);
            Assert.That(stream.Count % candidates.Length, Is.EqualTo(0));
            for (int start = 0; start < stream.Count; start += candidates.Length)
            {
                HashSet<string> cycle = new HashSet<string>(stream.Skip(start).Take(candidates.Length));
                Assert.That(cycle.SetEquals(expected), Is.True,
                    $"Sample stream repeated a type before exhausting [{string.Join(",", candidates)}] at offset {start}.");
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

        private object GetProperty(object instance, string propertyName)
        {
            PropertyInfo property = _optionsType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            return property.GetValue(instance);
        }
    }
}
