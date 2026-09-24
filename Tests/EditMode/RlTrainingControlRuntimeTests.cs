using System;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlTrainingControlRuntimeTests
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [TestCase(true, "training", false)]
        [TestCase(true, "inference", true)]
        [TestCase(true, "stopped", true)]
        [TestCase(false, "training", true)]
        public void ShouldForceInferenceMatchesLeaseAndDesiredMode(
            bool online,
            string desiredMode,
            bool expected)
        {
            Type runtime = RuntimeAssembly.GetType("RlTrainingControlRuntime");
            Assert.That(runtime, Is.Not.Null);
            MethodInfo method = runtime.GetMethod("ShouldForceInference", StaticFlags);
            Assert.That(method, Is.Not.Null);

            Assert.That(
                (bool)method.Invoke(null, new object[] { online, desiredMode }),
                Is.EqualTo(expected));
        }

        [Test]
        public void TryParseStateAcceptsManagedTrainingState()
        {
            Type runtime = RuntimeAssembly.GetType("RlTrainingControlRuntime");
            MethodInfo parse = runtime.GetMethod("TryParseState", StaticFlags);
            Assert.That(parse, Is.Not.Null);

            object[] arguments =
            {
                "{\"online\":true,\"desired_mode\":\"training\"}",
                true
            };
            Assert.That((bool)parse.Invoke(null, arguments), Is.True);
            Assert.That((bool)arguments[1], Is.False);
        }

        [Test]
        public void TryParseStateFailsClosedForMalformedOrUnknownState()
        {
            Type runtime = RuntimeAssembly.GetType("RlTrainingControlRuntime");
            MethodInfo parse = runtime.GetMethod("TryParseState", StaticFlags);
            Assert.That(parse, Is.Not.Null);

            object[] unknown =
            {
                "{\"online\":true,\"desired_mode\":\"unknown\"}",
                false
            };
            Assert.That((bool)parse.Invoke(null, unknown), Is.False);
            Assert.That((bool)unknown[1], Is.True);

            object[] malformed = { "not-json", false };
            Assert.That((bool)parse.Invoke(null, malformed), Is.False);
            Assert.That((bool)malformed[1], Is.True);
        }
    }
}
