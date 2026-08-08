using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ServerMatchupIdSerializationTests
    {
        private const string UnsignedMatchupId = "18446744073709551610";
        private const string UnsignedShootingMatchupId = "18446744073709551611";

        [Test]
        public void CommandResponsePreservesUnsignedMatchupIdsAsExactStrings()
        {
            string json = "{\"MatchupId\":\"" + UnsignedMatchupId +
                "\",\"ShootingStrategyMatchupId\":\"" + UnsignedShootingMatchupId + "\"}";
            object response = JsonUtility.FromJson(
                json, RuntimeAssembly.GetType("Assets.Scripts.Server.CommandResponse"));

            Assert.That(RuntimeAssembly.GetField(response, "MatchupId"), Is.EqualTo(UnsignedMatchupId));
            Assert.That(RuntimeAssembly.GetField(response, "ShootingStrategyMatchupId"),
                Is.EqualTo(UnsignedShootingMatchupId));
        }

        [Test]
        public void MatchupStrategyResponsePreservesUnsignedMatchupIdAsExactString()
        {
            string json = "{\"MatchupId\":\"" + UnsignedMatchupId + "\"}";
            object response = JsonUtility.FromJson(
                json, RuntimeAssembly.GetType("Assets.Scripts.Server.MatchupStrategyResponse"));

            Assert.That(RuntimeAssembly.GetField(response, "MatchupId"), Is.EqualTo(UnsignedMatchupId));
        }
    }
}
