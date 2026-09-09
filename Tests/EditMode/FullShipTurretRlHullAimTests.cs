using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class FullShipTurretRlFireControlTests
    {
        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < pathParts.Length; i++)
            {
                path = Path.Combine(path, pathParts[i]);
            }
            return File.ReadAllText(path);
        }

        [Test]
        public void RlMainCannonNeverClaimsOrSuppressesHullMovement()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Weapons", "FullShipTurret.cs");

            Assert.That(source, Does.Not.Contain("_rlHullAimActive"));
            Assert.That(source, Does.Not.Contain("BeginRlHullAim"));
            Assert.That(source, Does.Not.Contain("MaintainRlHullAim"));
            Assert.That(source, Does.Not.Contain("ReleaseRlHullAim"));
            Assert.That(source, Does.Not.Contain("Ship.CurrentSpeed = 0f;"));
            Assert.That(source, Does.Not.Contain("Ship.RotationSpeed = 0f;"));
            Assert.That(source, Does.Not.Contain("Ship.Body.linearVelocity = Vector2.zero;"));
            Assert.That(source, Does.Contain("IsAimedAtTarget = Utilities.IsRotatedTowards(this, GetDegreesTowardsPoint(TargetPoint));"));
        }

        [Test]
        public void RlMainCannonMayCommitReadyShotRegardlessOfAlignment()
        {
            string targeting = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");
            string fullShipTurret = ReadSource("Scripts", "Entities", "Ships", "Weapons", "FullShipTurret.cs");

            Assert.That(targeting, Does.Contain("ReadyToFire && RlFireRequested && CanAcceptRlFireRequest() && !Ship.IsCeaseFire"));
            Assert.That(targeting, Does.Contain("protected virtual bool CanAcceptRlFireRequest()"));
            Assert.That(targeting, Does.Contain("return IsAimedAtTarget;"));
            Assert.That(fullShipTurret, Does.Contain("protected override bool CanAcceptRlFireRequest()"));
            Assert.That(fullShipTurret, Does.Contain("return true;"));
        }

        [Test]
        public void QueuedRlMainCannonShotUsesCurrentPhysicalHeadingAtFireTime()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Weapons", "FullShipTurret.cs");

            Assert.That(source, Does.Contain("TargetPoint = GetRlForwardFirePoint();"));
            Assert.That(source, Does.Contain("float radians = Rotation * Mathf.Deg2Rad;"));
            Assert.That(source, Does.Contain("new Vector2(-Mathf.Sin(radians), Mathf.Cos(radians))"));
            Assert.That(source, Does.Not.Contain("_rlCommittedTargetPoint"));
            Assert.That(source, Does.Not.Contain("return Utilities.IsRotatedTowards(this, GetDegreesTowardsPoint(TargetPoint));"));
        }

        [Test]
        public void AuthoredCooldownChargeAndSafetyChecksStillGateActualShot()
        {
            string targeting = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");
            string laserBuilder = ReadSource("Scripts", "Entities", "Ships", "Weapons", "LaserBuilder.cs");

            Assert.That(targeting, Does.Contain("if (!ReadyToFire)"));
            Assert.That(targeting, Does.Contain("FireAtPoint();"));
            Assert.That(laserBuilder, Does.Contain("_rlShotQueued = true;"));
            Assert.That(laserBuilder, Does.Contain("bool canShoot = !Ship.IsDead && !Ship.IsCeaseFire"));
            Assert.That(laserBuilder, Does.Contain("CanCompleteQueuedShot();"));
        }
    }
}