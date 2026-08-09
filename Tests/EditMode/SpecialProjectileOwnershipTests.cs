using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SpecialProjectileOwnershipTests
    {
        [Test]
        public void LaserBeamAlwaysReleasesShooterRegistration()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "LaserBeam.cs");
            string source = File.ReadAllText(path);
            StringAssert.Contains("Shooter?.ProjectilesInFlight.Remove(this);", source);
            StringAssert.DoesNotContain("if (!ShipIsDead)", source);
        }

        [Test]
        public void SplitChildrenRetainDeadShooterUntilTheyFinish()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "SplitterShot.cs");
            string source = File.ReadAllText(path);

            int add = source.IndexOf("Shooter.ProjectilesInFlight.Add(_projectile);");
            int deadCheck = source.IndexOf("if (Shooter.IsDead)", add);
            Assert.That(add, Is.GreaterThanOrEqualTo(0));
            Assert.That(deadCheck, Is.GreaterThan(add));
            StringAssert.Contains("_projectile.ShipIsDead = true;", source.Substring(deadCheck));
        }
    }
}
