using System;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlTrainingReadinessTests
    {
        [Test]
        public void CanonicalPolicyAbiIdentifiesThe512By3Network()
        {
            Type schemaType = RuntimeAssembly.GetType("RlPolicySchema");

            Assert.That((int)RuntimeAssembly.GetStaticField(schemaType, "Version"), Is.EqualTo(4));
            string signature = (string)RuntimeAssembly.GetStaticField(schemaType, "Signature");
            StringAssert.StartsWith("bees-rl-v4|", signature);
            StringAssert.Contains("network=ff-512x3", signature);
        }

        [Test]
        public void StaleShipBindingCanBeReusedWithinTheSameEpisode()
        {
            object agent = RuntimeAssembly.CreateUninitialized("RlOneVsOneAgent");
            RuntimeAssembly.SetField(agent, "_hasBoundShip", true);
            RuntimeAssembly.SetField(agent, "_hasParticipatedThisEpisode", true);
            RuntimeAssembly.SetField(agent, "_boundRuntimeShipId", 12345L);
            RuntimeAssembly.SetField(agent, "_decisionCounter", 4);
            RuntimeAssembly.SetField(agent, "_nextMiningActionTime", 12f);
            RuntimeAssembly.SetField(agent, "_nextHealingActionTime", 13f);

            RuntimeAssembly.Invoke(agent, "ReleaseStaleShipBinding");

            Assert.That((bool)RuntimeAssembly.GetField(agent, "_hasBoundShip"), Is.False);
            Assert.That((long)RuntimeAssembly.GetField(agent, "_boundRuntimeShipId"), Is.Zero);
            Assert.That((int)RuntimeAssembly.GetField(agent, "_decisionCounter"), Is.Zero);
            Assert.That((float)RuntimeAssembly.GetField(agent, "_nextMiningActionTime"), Is.Zero);
            Assert.That((float)RuntimeAssembly.GetField(agent, "_nextHealingActionTime"), Is.Zero);
            Assert.That((bool)RuntimeAssembly.GetField(agent, "_hasParticipatedThisEpisode"), Is.True,
                "Reusing a controller must not erase its cooperative reward participation for the current battle.");
        }
    }
}
