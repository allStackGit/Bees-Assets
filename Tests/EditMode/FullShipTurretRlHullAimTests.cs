using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class FullShipTurretRlHullAimTests
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
        public void RlMainCannonOnlyClaimsHullWhenReadyToCommitAShot()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Weapons", "FullShipTurret.cs");

            Assert.That(source, Does.Contain("IsRlShotQueued || (RlFireRequested && ReadyToFire && !Ship.IsCeaseFire)"));
            Assert.That(source, Does.Contain("Ship.CurrentSpeed = 0f;"));
            Assert.That(source, Does.Contain("Ship.RotationSpeed = 0f;"));
            Assert.That(source, Does.Contain("Ship.Body.linearVelocity = Vector2.zero;"));
            Assert.That(source, Does.Contain("Ship.CurrentSpeed = _rlPreviousCurrentSpeed;"));
            Assert.That(source, Does.Contain("Ship.RotationSpeed = _rlPreviousRotationSpeed;"));
        }

        [Test]
        public void AcceptedRlShotLatchesTargetUntilAnimationResolves()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Weapons", "FullShipTurret.cs");

            Assert.That(source, Does.Contain("_rlCommittedTargetPoint = TargetPoint;"));
            Assert.That(source, Does.Contain("TargetPoint = IsRlShotQueued ? _rlCommittedTargetPoint : RlTargetPoint;"));
            Assert.That(source, Does.Contain("TargetPoint = _rlCommittedTargetPoint;"));
        }

        [Test]
        public void AnimationEventRevalidatesAimAndReleasesHullOwnership()
        {
            string laserBuilder = ReadSource("Scripts", "Entities", "Ships", "Weapons", "LaserBuilder.cs");
            string fullShipTurret = ReadSource("Scripts", "Entities", "Ships", "Weapons", "FullShipTurret.cs");

            Assert.That(laserBuilder, Does.Contain("CanCompleteQueuedShot();"));
            Assert.That(laserBuilder, Does.Contain("OnShotResolved(fired);"));
            Assert.That(fullShipTurret, Does.Contain("protected override bool CanCompleteQueuedShot()"));
            Assert.That(fullShipTurret, Does.Contain("return Utilities.IsRotatedTowards(this, GetDegreesTowardsPoint(TargetPoint));"));
            Assert.That(fullShipTurret, Does.Contain("protected override void OnShotResolved(bool fired)"));
            Assert.That(fullShipTurret, Does.Contain("ReleaseRlHullAim();"));
        }

        [Test]
        public void ClearingRlControlCanReleaseUnqueuedHullCommitment()
        {
            string rlControl = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.RlControl.cs");
            string fullShipTurret = ReadSource("Scripts", "Entities", "Ships", "Weapons", "FullShipTurret.cs");

            Assert.That(rlControl, Does.Contain("OnRlControlUpdated();"));
            Assert.That(rlControl, Does.Contain("OnRlControlCleared();"));
            Assert.That(fullShipTurret, Does.Contain("protected override void OnRlControlCleared()"));
        }
    }
}
