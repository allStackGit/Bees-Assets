using System;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ShootingMatchupProtocolTests
    {
        [Test]
        public void ShootingIdentityPreservesCanonicalTwoSegmentShape()
        {
            Type requestType = RuntimeAssembly.GetType("Assets.Scripts.Server.CommandRequest");

            string identity = (string)RuntimeAssembly.InvokeStatic(
                requestType,
                "BuildShootingMatchupIdentity",
                "ABC",
                "ABCDEF|XYZ|1|2");

            Assert.That(identity, Is.EqualTo("ABC|XYZ|"),
                "The server enemy-type parser requires a trailing delimiter after the enemy segment.");
        }

        [Test]
        public void ShootingIdentityPreservesEmptyEnemySegment()
        {
            Type requestType = RuntimeAssembly.GetType("Assets.Scripts.Server.CommandRequest");

            string identity = (string)RuntimeAssembly.InvokeStatic(
                requestType,
                "BuildShootingMatchupIdentity",
                "ABC",
                "ABCDEF||0|0");

            Assert.That(identity, Is.EqualTo("ABC||"));
        }
    }
}
