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
    }
}
