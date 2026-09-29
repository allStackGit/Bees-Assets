using System;
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
        public void PooledStaticObstacleReuseClearsRlContactLethality()
        {
            Type staticObstacleType = Type.GetType("Assets.Scripts.Entities.StaticObstacle, Assembly-CSharp");
            Assert.That(staticObstacleType, Is.Not.Null);

            GameObject obstacleObject = new GameObject(nameof(PooledStaticObstacleReuseClearsRlContactLethality));
            try
            {
                Component obstacle = obstacleObject.AddComponent(staticObstacleType);
                var killsShipsOnContact = staticObstacleType.GetProperty("KillsShipsOnContact");
                var resetForReuse = staticObstacleType.GetMethod("ResetForReuse");

                Assert.That(killsShipsOnContact, Is.Not.Null);
                Assert.That(resetForReuse, Is.Not.Null);

                killsShipsOnContact.SetValue(obstacle, true);
                resetForReuse.Invoke(obstacle, null);

                Assert.That((bool)killsShipsOnContact.GetValue(obstacle), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(obstacleObject);
            }
        }

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
