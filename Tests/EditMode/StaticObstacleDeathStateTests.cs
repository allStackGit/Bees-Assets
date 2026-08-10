using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class StaticObstacleDeathStateTests
    {
        [Test]
        public void BaseObstacleMarksDeadBeforeDeferredUnityDestroy()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Obstacle.cs"));
            int kill = source.IndexOf("public virtual void Kill()");
            int markDead = source.IndexOf("IsDead = true;", kill);
            int destroy = source.IndexOf("Destroy(gameObject);", kill);

            Assert.That(kill, Is.GreaterThanOrEqualTo(0));
            Assert.That(markDead, Is.GreaterThan(kill));
            Assert.That(destroy, Is.GreaterThan(markDead));
        }
    }
}
