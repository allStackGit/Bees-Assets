using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PooledShipReferenceCleanupTests
    {
        [Test]
        public void RemoveShipClearsDamageAndSpottingReferencesBeforeRelease()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf("public void RemoveShip(Ship ship)");
            int methodEnd = source.IndexOf("public void AddDeadBody", methodStart);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, methodEnd - methodStart);
            int damageCleanup = method.IndexOf("statuses.RemoveAll");
            int spottedCleanup = method.IndexOf("spotted.RemoveAll");
            int releaseQueue = method.IndexOf("ShipsToRelease.Add(ship)");

            Assert.That(damageCleanup, Is.GreaterThanOrEqualTo(0));
            Assert.That(spottedCleanup, Is.GreaterThan(damageCleanup));
            Assert.That(releaseQueue, Is.GreaterThan(spottedCleanup));
            StringAssert.Contains("status.Ship == ship", method);
            StringAssert.Contains("entry.Ship == ship", method);
        }

        [Test]
        public void CarrierDeathCleansReferencesForItsOwnSide()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Carrier.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("Level.State.GetShips(Side)", source);
            StringAssert.DoesNotContain("Level.State.GetHumanShips()", source);
            StringAssert.Contains(".Where(ship => ship.Carrier == this)", source);
            StringAssert.Contains("carrierShip.Carrier = null;", source);
        }
    }
}
