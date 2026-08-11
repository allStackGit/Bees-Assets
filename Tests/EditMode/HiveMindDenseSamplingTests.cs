using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class HiveMindDenseSamplingTests
    {
        [Test]
        public void PotentialEnemyCapUsesDeterministicOrderingBeforeTake()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.Combat.cs"));

            int method = source.IndexOf("GetPotentialEnemies");
            int order = source.IndexOf("OrderBy(ship => ship.DistanceToPoint(origin))", method);
            int typeOrder = source.IndexOf("ThenBy(ship => ship.ShipType)", method);
            int idOrder = source.IndexOf("ThenBy(ship => ship.Id)", method);
            int cap = source.IndexOf("Take(64)", method);

            Assert.That(order, Is.GreaterThan(method));
            Assert.That(typeOrder, Is.GreaterThan(order));
            Assert.That(idOrder, Is.GreaterThan(typeOrder));
            Assert.That(cap, Is.GreaterThan(idOrder),
                "The set-backed visible-ship collection must be deterministically ordered before applying the 64-ship cap.");
        }
    }
}
