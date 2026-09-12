using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TurretLifecycleTests
    {
        [Test]
        public void TurretLifecycleResetClearsTargetingCadence()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Turret.cs"));
            StringAssert.Contains("public partial class Turret : Weapon", source);
            StringAssert.Contains("TargetingPasses = 0;", source);
        }

        [Test]
        public void TurretTargetingAndAimingAreSeparatePartials()
        {
            string folder = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons");
            string aiming = File.ReadAllText(Path.Combine(folder, "Turret.Aiming.cs"));
            string targeting = File.ReadAllText(Path.Combine(folder, "Turret.Targeting.cs"));

            StringAssert.Contains("public partial class Turret", aiming);
            StringAssert.Contains("protected virtual void Aim()", aiming);
            StringAssert.Contains("protected Vector2 GetTargetPoint", aiming);
            StringAssert.Contains("public partial class Turret", targeting);
            StringAssert.Contains("private void TargetingSequence()", targeting);
            StringAssert.Contains("protected void TryToFire()", targeting);
        }

        [Test]
        public void IdleTurretChecksRotationBeforeScanningTargets()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "Turret.Aiming.cs"));

            StringAssert.Contains(
                "if (Rotation != Ship.Rotation && (Ship.IsCeaseFire || !HasValidTarget()))",
                source);
        }

        [TestCase("LaserBuilder.cs")]
        [TestCase("BeamCannon.cs")]
        [TestCase("FullShipTurret.cs")]
        public void SpecializedTurretRlAimPrecedesMouseInput(string filename)
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                filename));

            int aim = source.IndexOf("protected override void Aim()");
            int rlControl = source.IndexOf("if (IsRlControlled)", aim);
            int mouseInput = source.IndexOf("Stage.InputManager.GetMousePosition()", aim);

            Assert.That(aim, Is.GreaterThanOrEqualTo(0));
            Assert.That(rlControl, Is.GreaterThan(aim), $"{filename} must handle RL control inside Aim().");
            Assert.That(mouseInput, Is.GreaterThan(rlControl), $"{filename} must not route RL control through mouse input.");
        }

        [Test]
        public void LaserBuilderRlFireIsQueuedByTheTurretFireGate()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "LaserBuilder.cs"));

            int sendProjectile = source.IndexOf("protected override void SendProjectile()");
            int actuallyShoot = source.IndexOf("public void ActuallyShoot()", sendProjectile);
            Assert.That(sendProjectile, Is.GreaterThanOrEqualTo(0));
            Assert.That(actuallyShoot, Is.GreaterThan(sendProjectile));

            string queuedFirePath = source.Substring(sendProjectile, actuallyShoot - sendProjectile);
            StringAssert.Contains("if (IsRlControlled)", queuedFirePath);
            StringAssert.Contains("_rlShotQueued = true;", queuedFirePath);
            StringAssert.Contains("LaserBuilderAnimation.SetActive(true);", queuedFirePath);
            StringAssert.Contains("bool directPointFire = IsRlControlled ? _rlShotQueued : IsFiringManually;", source);
        }

        [Test]
        public void FullShipTurretKeepsLogicalTurretHeadingsSynchronizedWithHullTurns()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "FullShipTurret.cs"));

            StringAssert.Contains("RotateShipAndTurrets(_rightRotationRate);", source);
            StringAssert.Contains("RotateShipAndTurrets(_leftRotationRate);", source);
            StringAssert.Contains("Ship.Turrets.ForEach((turret) => turret.Rotation += rotationDelta.z);", source);
            StringAssert.Contains("float rotationDelta = Mathf.DeltaAngle(Ship.Rotation, rotation);", source);
            StringAssert.Contains("Ship.Turrets.ForEach((turret) => turret.Rotation += rotationDelta);", source);
        }

        [Test]
        public void FullShipTurretLeavesRlChargeAnimationOwnedByLaserBuilderQueue()
        {
            string fullShipTurret = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "FullShipTurret.cs"));
            string laserBuilder = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "LaserBuilder.cs"));

            int rlAimStart = fullShipTurret.IndexOf("private void AimRlMainCannon()");
            int nextMethod = fullShipTurret.IndexOf("protected override void SendProjectile()", rlAimStart);
            Assert.That(rlAimStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(nextMethod, Is.GreaterThan(rlAimStart));
            string rlAim = fullShipTurret.Substring(rlAimStart, nextMethod - rlAimStart);
            StringAssert.DoesNotContain("LaserBuilderAnimation.SetActive(true);", rlAim,
                "Aiming the RL main cannon must not start the charge animation before a shot is accepted.");

            int sendProjectile = fullShipTurret.IndexOf("protected override void SendProjectile()");
            int baseFire = fullShipTurret.IndexOf("base.SendProjectile();", sendProjectile);
            int nonRlSendGuard = fullShipTurret.IndexOf("if (!IsRlControlled)", baseFire);
            int sendDeactivation = fullShipTurret.IndexOf("LaserBuilderAnimation.SetActive(false);", nonRlSendGuard);
            Assert.That(sendProjectile, Is.GreaterThanOrEqualTo(0));
            Assert.That(baseFire, Is.GreaterThan(sendProjectile));
            Assert.That(nonRlSendGuard, Is.GreaterThan(baseFire));
            Assert.That(sendDeactivation, Is.GreaterThan(nonRlSendGuard));

            StringAssert.Contains("_rlShotQueued = true;", laserBuilder);
            StringAssert.Contains("LaserBuilderAnimation.SetActive(true);", laserBuilder,
                "LaserBuilder must remain the owner that starts the animation for an accepted queued RL shot.");
        }

        [Test]
        public void RlWeaponReadinessLatchesUntilARequestedShotFires()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Entities",
                "Ships",
                "Weapons",
                "Turret.Targeting.cs"));

            StringAssert.Contains("if (!ReadyToFire)", source);
            StringAssert.Contains("if (TargetingPasses >= PassesPerFire)", source);
            StringAssert.Contains("TargetingPasses = PassesPerFire;", source);
            StringAssert.Contains("ReadyToFire = true;", source);
            StringAssert.Contains("if (ReadyToFire && RlFireRequested && CanAcceptRlFireRequest() && !Ship.IsCeaseFire)", source);
            StringAssert.Contains("ReadyToFire = false;", source);
        }

        [Test]
        public void LatchedRlWeaponReadinessIsVersionedInThePolicyAbi()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Scenes",
                "RlPolicySchema.cs"));

            StringAssert.Contains("internal const int Version = 7;", source);
            StringAssert.Contains("bees-rl-v7", source);
            StringAssert.Contains("weapon-ready=rl-latched-until-fire", source);
        }
    }
}
