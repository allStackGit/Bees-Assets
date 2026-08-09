using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ProjectileSameFrameLifecycleTests
    {
        [Test]
        public void BaseProjectileStopsProcessingAfterCollisionKillsIt()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "Projectile.cs");
            string source = File.ReadAllText(path);

            int fixedUpdate = source.IndexOf("protected virtual void FixedUpdate()");
            int removeDamage = source.IndexOf("public virtual void RemoveDamageSentEntry", fixedUpdate);
            Assert.That(fixedUpdate, Is.GreaterThanOrEqualTo(0));
            Assert.That(removeDamage, Is.GreaterThan(fixedUpdate));

            string method = source.Substring(fixedUpdate, removeDamage - fixedUpdate);
            StringAssert.Contains("ShipCollision(CollidingQueue.Dequeue());", method);
            StringAssert.Contains("if (IsDead)", method);
            StringAssert.Contains("ContactObstacle(CollidingObstacleQueue.Dequeue());", method);
        }

        [TestCase("PowerShot.cs", "KillSequence();")]
        [TestCase("LaserBeam.cs", "Kill();")]
        public void PiercingProjectilesReturnImmediatelyAfterSelfKill(string fileName, string killCall)
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", fileName);
            string source = File.ReadAllText(path);
            int contact = source.IndexOf("public override void ContactTarget(Ship target)");
            int powerSubtract = source.IndexOf("Power -= _powerLoss", contact);
            Assert.That(contact, Is.GreaterThanOrEqualTo(0));
            Assert.That(powerSubtract, Is.GreaterThan(contact));

            string prefix = source.Substring(contact, powerSubtract - contact);
            int kill = prefix.IndexOf(killCall);
            int returnIndex = prefix.IndexOf("return;", kill);
            Assert.That(kill, Is.GreaterThanOrEqualTo(0));
            Assert.That(returnIndex, Is.GreaterThan(kill));
        }

        [Test]
        public void LaserBeamDoesNotExtendAfterBaseUpdateKillsIt()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "LaserBeam.cs");
            string source = File.ReadAllText(path);
            int fixedUpdate = source.IndexOf("protected override void FixedUpdate()");
            int extendMethod = source.IndexOf("private void ExtendBeam", fixedUpdate);
            string method = source.Substring(fixedUpdate, extendMethod - fixedUpdate);

            int baseUpdate = method.IndexOf("base.FixedUpdate();");
            int deadGuard = method.IndexOf("if (!IsDead)");
            int extend = method.IndexOf("ExtendBeam();");
            Assert.That(baseUpdate, Is.GreaterThanOrEqualTo(0));
            Assert.That(deadGuard, Is.GreaterThan(baseUpdate));
            Assert.That(extend, Is.GreaterThan(deadGuard));
        }
    }
}
