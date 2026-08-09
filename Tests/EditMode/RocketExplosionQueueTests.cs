using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RocketExplosionQueueTests
    {
        [Test]
        public void ExplosionDrainsShipAndObstacleContactQueues()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "RocketExplosion.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("while (CollidingQueue.Count > 0)", source);
            StringAssert.Contains("while (CollidingObstacleQueue.Count > 0)", source);
            StringAssert.DoesNotContain("_index < CollidingQueue.Count", source);
        }

        [Test]
        public void ExplosionRecordsObstacleBeforeApplyingDamage()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "RocketExplosion.cs");
            string source = File.ReadAllText(path);

            int record = source.IndexOf("_obstaclesHit.Add(obstacle)");
            int damage = source.IndexOf("DamageObstacle((CollisionAsteroid)obstacle)");
            Assert.That(record, Is.GreaterThanOrEqualTo(0));
            Assert.That(damage, Is.GreaterThan(record));
        }

        [Test]
        public void RocketTransfersItsSingleDamageReservationToExplosion()
        {
            string rocketPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "Rocket.cs");
            string projectilePath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "Projectile.cs");
            string explosionPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "RocketExplosion.cs");
            string rocketSource = File.ReadAllText(rocketPath);
            string projectileSource = File.ReadAllText(projectilePath);
            string explosionSource = File.ReadAllText(explosionPath);

            StringAssert.Contains("RocketExplosion.Setup(Level, Weapon, Shooter, null,", rocketSource);
            StringAssert.Contains("TransferDamageReservationTo(RocketExplosion);", rocketSource);
            StringAssert.Contains("recipient._damageReservation = _damageReservation", projectileSource);
            StringAssert.Contains("_damageReservation = null", projectileSource);
            StringAssert.DoesNotContain("if (Target != null)", explosionSource);
        }
    }
}