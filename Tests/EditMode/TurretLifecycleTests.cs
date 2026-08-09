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
    }
}
