using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlDecisionPeriodTests
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
        public void DefaultDecisionPeriodIsFive()
        {
            object options = Parse();
            Assert.That(GetProperty(options, "DecisionPeriod"), Is.EqualTo(5));
        }

        [Test]
        public void CommandLineDecisionPeriodOverridesDefault()
        {
            Assert.That(GetProperty(Parse("--rl-decision-period", "3"), "DecisionPeriod"), Is.EqualTo(3));
            Assert.That(GetProperty(Parse("--rl-decision-period=7"), "DecisionPeriod"), Is.EqualTo(7));
        }

        [Test]
        public void NonPositiveDecisionPeriodFailsFast()
        {
            AssertParseFails("--rl-decision-period", "0");
            AssertParseFails("--rl-decision-period", "-1");
        }

        [Test]
        public void RuntimeUsesOneAuthoritativeDecisionPeriodAndOneDecisionRequester()
        {
            string bootstrap = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            Assert.That(bootstrap, Does.Contain("CurrentDecisionPeriod => RuntimeOptions.DecisionPeriod"));
            Assert.That(agent, Does.Contain("Time.frameCount % RlOneVsOneTrainingBootstrap.CurrentDecisionPeriod"));
            Assert.That(agent, Does.Contain("_decisionCounter >= RlOneVsOneTrainingBootstrap.CurrentDecisionPeriod"));
            Assert.That(agent, Does.Contain("RequestDecision();"));
            Assert.That(agent, Does.Not.Contain("DecisionRequester"));
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
