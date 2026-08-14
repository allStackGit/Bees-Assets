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
            int distanceCache = source.IndexOf("_combatDistanceKeys[ship.Id] = ship.DistanceToPoint(origin);", method);
            int sort = source.IndexOf("_potentialEnemyShips.Sort(CompareCombatDistance);", method);
            int cap = source.IndexOf("_enemies.Count < 64", method);

            Assert.That(distanceCache, Is.GreaterThan(method));
            Assert.That(sort, Is.GreaterThan(distanceCache));
            Assert.That(cap, Is.GreaterThan(sort));
            Assert.That(source, Does.Contain("comparison = a.ShipType.CompareTo(b.ShipType);"));
            Assert.That(source, Does.Contain("return a.Id.CompareTo(b.Id);"),
                "The set-backed visible-ship collection must be deterministically ordered by distance, type, and id before applying the 64-ship cap.");
        }
    }
}
