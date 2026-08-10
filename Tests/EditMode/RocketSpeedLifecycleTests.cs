using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RocketSpeedLifecycleTests
    {
        [Test]
        public void RocketAccelerationHonorsAuthoredMaxSpeed()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Projectiles", "Rocket.cs"));
            Assert.That(source, Does.Contain("Vector2.ClampMagnitude(Body.linearVelocity * 1.5f, MaxSpeed)"));
        }

        [Test]
        public void FrigateMissilePrefabDefinesFiniteMaxSpeed()
        {
            string prefab = File.ReadAllText(Path.Combine(Application.dataPath, "Prefabs", "Entities", "Projectiles", "Frigate Missle Release.prefab"));
            Assert.That(prefab, Does.Contain("Speed: 60"));
            Assert.That(prefab, Does.Contain("MaxSpeed: 160"));
        }
    }
}
