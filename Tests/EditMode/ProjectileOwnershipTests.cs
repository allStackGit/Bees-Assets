using System;
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
            StringAssert.Contains("private int _reservedDamageAmount", source);
            StringAssert.Contains("_damageReservation = target != null ? Level.State.GetShipDamageStatus(shooter.Side, target) : null", source);
            StringAssert.Contains("_reservedDamageAmount = target != null ? power : 0", source);
            StringAssert.Contains("if (_damageReservation == null)", source);
            StringAssert.Contains("_damageReservation.TotalDamageSentToShip -= _reservedDamageAmount", source);
            StringAssert.Contains("_damageReservation = null", source);
            StringAssert.Contains("_reservedDamageAmount = 0", source);
            StringAssert.DoesNotContain("Level.State.GetShipDamageStatus(Shooter.Side, Target)", source);
        }

        [Test]
        public void LethalDamageDoesNotRecreatePurgedTargetDamageStatus()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public static void LogAttackingDamage", StringComparison.Ordinal);
            int end = source.IndexOf("protected static void LogHitStats", start, StringComparison.Ordinal);
            string method = source.Substring(start, end - start);
            int lethalBranch = method.IndexOf("if (target.Health == 0)", StringComparison.Ordinal);
            int returnAfterKill = method.IndexOf("return;", lethalBranch, StringComparison.Ordinal);
            string lethalPath = method.Substring(lethalBranch, returnAfterKill - lethalBranch);

            StringAssert.Contains("target.Kill(attacker, attackerFleetShip, attackerSavedSquad)", lethalPath);
            StringAssert.DoesNotContain("GetShipDamageStatus", lethalPath);
        }

        [Test]
        public void ActiveProjectilesForgetDepartedShipLifecycleBeforeWrapperReuse()
        {
            string projectile = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "Projectile.cs"));
            string powerShot = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "PowerShot.cs"));
            string laserBeam = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "LaserBeam.cs"));
            string explosion = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "RocketExplosion.cs"));
            string registry = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));

            StringAssert.Contains("public virtual void ForgetShip(Ship ship)", projectile);
            StringAssert.Contains("ReferenceEquals(_damageReservation.Ship, ship)", projectile);
            StringAssert.Contains("ReferenceEquals(Target, ship)", projectile);
            StringAssert.Contains("CollidingQueue.Where(candidate => !ReferenceEquals(candidate, ship))", projectile);
            StringAssert.Contains("public override void ForgetShip(Ship ship)", powerShot);
            StringAssert.Contains("public override void ForgetShip(Ship ship)", laserBeam);
            StringAssert.Contains("public override void ForgetShip(Ship ship)", explosion);
            StringAssert.Contains("projectile.ForgetShip(ship)", registry);
        }
    }
}
