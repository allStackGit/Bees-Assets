using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TargetQueueExhaustionTests
    {
        [Test]
        public void ShipTargetHelperDoesNotDequeueAnEmptyQueue()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public Ship SetAndGetTargetEnemy()");
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            string method = source.Substring(start);

            StringAssert.Contains("command.TargetingQueue.Count == 0", method);
            StringAssert.Contains("return null;", method);
            StringAssert.Contains("command.TargetingQueue.Dequeue()", method);
            Assert.That(method.IndexOf("return null;"), Is.LessThan(method.IndexOf("command.TargetingQueue.Dequeue()")));
        }

        [Test]
        public void SharedMovementFinalizesWhenTargetHelperReturnsNull()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Command.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public void MoveTowardsEnemies()");
            int end = source.IndexOf("protected void Timeout()", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string method = source.Substring(start, end - start);

            StringAssert.Contains("Ship target = ship.SetAndGetTargetEnemy();", method);
            StringAssert.Contains("if (target == null)", method);
            StringAssert.Contains("SetFinalize(\"No more enemy ships to target\")", method);
        }
    }
}
