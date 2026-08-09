using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ProjectileOwnershipTests
    {
        [Test]
        public void ProjectileAlwaysReleasesItsShooterOwnershipWhenKilled()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "Projectile.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("Shooter?.ProjectilesInFlight.Remove(this)", source);
            StringAssert.DoesNotContain("if (!ShipIsDead)", source);
        }

        [Test]
        public void DamageReservationCleanupUsesCapturedReservationInsteadOfMutableTargetWrapper()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "Projectile.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("private ShipDamageStatus _damageReservation", source);
            StringAssert.Contains("_damageReservation = target != null ? Level.State.GetShipDamageStatus(shooter.Side, target) : null", source);
            StringAssert.Contains("if (_damageReservation == null)", source);
            StringAssert.Contains("_damageReservation.TotalDamageSentToShip -= Power", source);
            StringAssert.Contains("_damageReservation = null", source);
            StringAssert.DoesNotContain("Level.State.GetShipDamageStatus(Shooter.Side, Target)", source);
        }
    }
}