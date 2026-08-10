using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PooledHashIdentityTests
    {
        private static string Read(params string[] parts)
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, Path.Combine(parts)));
        }

        [Test]
        public void StableReferenceComparerDoesNotDependOnMutableRuntimeId()
        {
            string source = Read("Scripts", "ReferenceIdentityComparer.cs");

            Assert.That(source, Does.Contain("ReferenceEquals(x, y)"));
            Assert.That(source, Does.Contain("RuntimeHelpers.GetHashCode(obj)"));
            Assert.That(source, Does.Not.Contain("obj.GetHashCode()"));
        }

        [TestCase("Scripts/Entities/Ships/ProximityCollider.cs", "NearbyEnemyShips")]
        [TestCase("Scripts/Levels/Zone.cs", "Ships")]
        [TestCase("Scripts/Entities/Ships/Beehive.cs", "ShipsHealingHere")]
        [TestCase("Scripts/Entities/Projectiles/PowerShot.cs", "_shipsHit")]
        [TestCase("Scripts/Entities/Projectiles/LaserBeam.cs", "_shipsHit")]
        [TestCase("Scripts/Levels/Commands/Charge.cs", "ChargingShips")]
        [TestCase("Scripts/Levels/Commands/BombingRun.cs", "ShipsCompletedCommand")]
        public void CrossFrameShipSetsUseReferenceIdentity(string relativePath, string fieldName)
        {
            string source = Read(relativePath.Split('/'));
            Assert.That(source, Does.Contain(fieldName));
            Assert.That(source, Does.Contain("ReferenceIdentityComparer<Ship>.Instance"));
        }

        [Test]
        public void CollisionAsteroidContactStateLivesInFocusedReferenceStablePartial()
        {
            string collisions = Read("Scripts", "Entities", "CollisionAsteroid.Collisions.cs");
            string core = Read("Scripts", "Entities", "CollisionAsteroid.cs");

            Assert.That(core, Does.Contain("public partial class CollisionAsteroid"));
            Assert.That(collisions, Does.Contain("public partial class CollisionAsteroid"));
            Assert.That(collisions, Does.Contain("ReferenceIdentityComparer<Ship>.Instance"));
            Assert.That(collisions, Does.Contain("ReferenceIdentityComparer<CollisionAsteroid>.Instance"));
            Assert.That(core, Does.Not.Contain("public void ShipCollision(Ship ship)"));
            Assert.That(collisions, Does.Contain("public void ShipCollision(Ship ship)"));
        }

        [Test]
        public void HivemindVisibilityUsesStableSetsAndCleansDepartingShips()
        {
            string state = Read("Scripts", "Levels", "GameState.cs");
            string registry = Read("Scripts", "Levels", "GameState.Registry.cs");
            string queries = Read("Scripts", "Levels", "GameState.Queries.cs");

            Assert.That(state, Does.Contain("ReferenceIdentityComparer<Ship>.Instance"));
            Assert.That(registry, Does.Contain("observerMap.Remove(ship.Id)"));
            Assert.That(registry, Does.Contain("visibleShips?.Remove(ship)"));
            Assert.That(queries, Does.Contain("new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance)"));
        }
    }
}
