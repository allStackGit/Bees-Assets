using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class StrikerBombTargetLifecycleTests
    {
        [Test]
        public void DelayedBombDamageRequiresOriginalContactedShipLifecycle()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "StrikerBomb.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("private long _contactedShipRuntimeId", source);
            StringAssert.Contains("_contactedShipRuntimeId = contactedShip.Id", source);
            StringAssert.Contains("ContactedShip.Id == _contactedShipRuntimeId", source);
            StringAssert.Contains("if (!HasOriginalContactedShip())", source);
        }

        [Test]
        public void BombStopsFollowingDeadOrReusedContactedWrapper()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "StrikerBomb.cs");
            string source = File.ReadAllText(path);

            int fixedUpdate = source.IndexOf("protected override void FixedUpdate()");
            Assert.That(fixedUpdate, Is.GreaterThanOrEqualTo(0));
            string method = source.Substring(fixedUpdate);
            StringAssert.Contains("!HasOriginalContactedShip()", method);
            StringAssert.Contains("Kill();", method);
        }
    }
}