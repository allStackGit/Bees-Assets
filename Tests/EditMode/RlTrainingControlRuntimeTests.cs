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
                "{\"schema_version\":1,\"online\":true,\"desired_mode\":\"training\"," +
                "\"updated_unix_seconds\":1000,\"lease_seconds\":20}",
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
                "{\"schema_version\":1,\"online\":true,\"desired_mode\":\"unknown\"," +
                "\"updated_unix_seconds\":1000,\"lease_seconds\":20}",
                false
            };
            Assert.That((bool)parse.Invoke(null, unknown), Is.False);
            Assert.That((bool)unknown[1], Is.True);

            object[] malformed = { "not-json", false };
            Assert.That((bool)parse.Invoke(null, malformed), Is.False);
            Assert.That((bool)malformed[1], Is.True);
        }

        [Test]
        public void TryParseStateAtTimeForcesInferenceWhenLocalLeaseIsStale()
        {
            Type runtime = RuntimeAssembly.GetType("RlTrainingControlRuntime");
            MethodInfo parse = runtime.GetMethod("TryParseStateAtTime", StaticFlags);
            Assert.That(parse, Is.Not.Null);

            object[] current =
            {
                "{\"schema_version\":1,\"online\":true,\"desired_mode\":\"training\"," +
                "\"updated_unix_seconds\":1000,\"lease_seconds\":20}",
                1019d,
                true
            };
            Assert.That((bool)parse.Invoke(null, current), Is.True);
            Assert.That((bool)current[2], Is.False);

            object[] stale =
            {
                "{\"schema_version\":1,\"online\":true,\"desired_mode\":\"training\"," +
                "\"updated_unix_seconds\":1000,\"lease_seconds\":20}",
                1021d,
                false
            };
            Assert.That((bool)parse.Invoke(null, stale), Is.True);
            Assert.That((bool)stale[2], Is.True);
        }

        [Test]
        public void TryParseStateFailsClosedWhenLeaseMetadataIsMissing()
        {
            Type runtime = RuntimeAssembly.GetType("RlTrainingControlRuntime");
            MethodInfo parse = runtime.GetMethod("TryParseStateAtTime", StaticFlags);
            Assert.That(parse, Is.Not.Null);

            object[] arguments =
            {
                "{\"schema_version\":1,\"online\":true,\"desired_mode\":\"training\"}",
                1000d,
                false
            };
            Assert.That((bool)parse.Invoke(null, arguments), Is.False);
            Assert.That((bool)arguments[2], Is.True);
        }


        [Test]
        public void TryParseStateAtTimeFailsClosedForUnsupportedSchemaOrLargeFutureSkew()
        {
            Type runtime = RuntimeAssembly.GetType("RlTrainingControlRuntime");
            MethodInfo parse = runtime.GetMethod("TryParseStateAtTime", StaticFlags);
            Assert.That(parse, Is.Not.Null);

            object[] wrongSchema =
            {
                "{\"schema_version\":2,\"online\":true,\"desired_mode\":\"training\"," +
                "\"updated_unix_seconds\":1000,\"lease_seconds\":20}",
                1000d,
                false
            };
            Assert.That((bool)parse.Invoke(null, wrongSchema), Is.False);
            Assert.That((bool)wrongSchema[2], Is.True);

            object[] futureSkew =
            {
                "{\"schema_version\":1,\"online\":true,\"desired_mode\":\"training\"," +
                "\"updated_unix_seconds\":1050,\"lease_seconds\":20}",
                1000d,
                false
            };
            Assert.That((bool)parse.Invoke(null, futureSkew), Is.True);
            Assert.That((bool)futureSkew[2], Is.True);
        }

    }
}
