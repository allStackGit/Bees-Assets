using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class BeaconProximityLifecycleTests
    {
        [Test]
        public void BeaconStatusIgnoresDeadOrDestroyedProximityEntries()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Beacon.cs");
            string source = File.ReadAllText(path);

            int method = source.IndexOf("public void SetBeaconStatus()");
            Assert.That(method, Is.GreaterThanOrEqualTo(0));
            StringAssert.Contains("NearbyEnemyShips.RemoveWhere(ship => ship == null || ship.IsDead)", source.Substring(method));
        }
    }
}