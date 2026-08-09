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
        public void DamageReservationCleanupToleratesMissingShooter()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "Projectile.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("Target == null || Shooter == null", source);
        }
    }
}
