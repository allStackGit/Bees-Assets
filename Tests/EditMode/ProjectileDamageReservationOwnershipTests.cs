using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ProjectileDamageReservationOwnershipTests
    {
        private static string Read(params string[] path) => File.ReadAllText(Path.Combine(Application.dataPath, Path.Combine(path)));

        [Test]
        public void ProjectileCleanupUsesOriginalReservedAmountWhenPowerChanges()
        {
            string source = Read("Scripts", "Entities", "Projectiles", "Projectile.cs");

            Assert.That(source, Does.Contain("private int _reservedDamageAmount;"));
            Assert.That(source, Does.Contain("_reservedDamageAmount = target != null ? power : 0;"));
            Assert.That(source, Does.Contain("recipient._reservedDamageAmount = _reservedDamageAmount;"));
            Assert.That(source, Does.Contain("TotalDamageSentToShip -= _reservedDamageAmount;"));
        }

        [TestCase("Turret.Aiming.cs", "base.SendProjectile();")]
        [TestCase("DualCannon.cs", "Level.AddProjectile(ConfigData.ProjectileTypes.HumanSmall")]
        [TestCase("BeamCannon.cs", "_beam.Setup(Level, this, Ship, TargetShip")]
        public void UntargetedFireClearsStaleTargetBeforeProjectileOwnership(string filename, string setupMarker)
        {
            string source = Read("Scripts", "Entities", "Ships", "Weapons", filename);
            int guard = source.IndexOf("if (IsFiringManually || IsFiringAtAsteroid)", StringComparison.Ordinal);
            int clear = source.IndexOf("SetTargetShipNull();", guard, StringComparison.Ordinal);
            int setup = source.IndexOf(setupMarker, clear, StringComparison.Ordinal);

            Assert.That(guard, Is.GreaterThanOrEqualTo(0));
            Assert.That(clear, Is.GreaterThan(guard));
            Assert.That(setup, Is.GreaterThan(clear));
        }
    }
}
