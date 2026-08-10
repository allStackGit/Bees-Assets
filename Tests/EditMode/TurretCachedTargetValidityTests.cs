using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TurretCachedTargetValidityTests
    {
        [Test]
        public void AimedAlternateTargetMustStillBeValidBeforeImmediateFire()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs"));
            int method = source.IndexOf("protected Ship GetAimedAtTarget()");
            int nextMethod = source.IndexOf("protected void Fire()", method);
            string body = source.Substring(method, nextMethod - method);

            Assert.That(body, Does.Contain("IsShipValidTarget(ship)"));
            Assert.That(body, Does.Not.Contain("if (!ship.IsDead &&"));
        }

        [Test]
        public void CurrentTurretTargetMustRemainValidBeforeFiring()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Turret.cs"));

            Assert.That(source, Does.Contain("public override bool ShouldFire"));
            Assert.That(source, Does.Contain("IsShipValidTarget(TargetShip)"));
        }

        [Test]
        public void LeastHealthStrategyOrdersByRemainingHealth()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Weapon.cs"));
            int strategy = source.IndexOf("case ConfigData.ShootingStrategyTypes.LeastHealth:");
            int nextStrategy = source.IndexOf("case ConfigData.ShootingStrategyTypes.MostHealth:", strategy);
            string body = source.Substring(strategy, nextStrategy - strategy);

            Assert.That(body, Does.Contain("a.Health.CompareTo(b.Health)"));
            Assert.That(body, Does.Not.Contain("Health - a.OriginalHealth"));
            Assert.That(body, Does.Not.Contain("Health - b.OriginalHealth"));
        }

        [Test]
        public void LaserBuilderRevalidatesBeforeAnimationEventFires()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "LaserBuilder.cs"));
            int method = source.IndexOf("public void ActuallyShoot()");
            int nextMethod = source.IndexOf("protected override void SetTargetShip", method);
            string body = source.Substring(method, nextMethod - method);

            Assert.That(body, Does.Contain("!Ship.IsDead"));
            Assert.That(body, Does.Contain("!Ship.IsCeaseFire"));
            Assert.That(body, Does.Contain("ShouldFireAtAsteroid"));
            Assert.That(body, Does.Contain("ShouldFire"));
            Assert.That(body, Does.Contain("if (canShoot)"));
        }
    }
}
