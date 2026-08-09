using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class FireTankExplosionOwnershipTests
    {
        [Test]
        public void FireTankExplosionKeepsShooterReservedUntilExplosionEnds()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "CanisterBomb.cs");
            string source = File.ReadAllText(path);

            int setup = source.IndexOf("Explosion.Setup(");
            int ownership = source.IndexOf("Explosion.Shooter.ProjectilesInFlight.Add(Explosion)");
            Assert.That(setup, Is.GreaterThanOrEqualTo(0));
            Assert.That(ownership, Is.GreaterThan(setup));
        }
    }
}
