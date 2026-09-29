using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class QueenCoroutineLifecycleTests
    {
        [Test]
        public void QueenCancelsDelayedMinionCoroutinesAtLifecycleBoundaries()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts", "Entities", "Ships", "Queen.cs"));

            int clearDataStart = source.IndexOf("public override void ClearData()");
            int dropExplosionStart = source.IndexOf("protected override void DropExplosionAnimation()", clearDataStart);
            string clearData = source.Substring(clearDataStart, dropExplosionStart - clearDataStart);
            Assert.That(clearData, Does.Contain("StopAllCoroutines();"),
                "A pooled Queen can inherit a delayed SpawnMinion coroutine from its prior lifecycle.");

            int killStart = source.IndexOf("public override void Kill(");
            string kill = source.Substring(killStart);
            Assert.That(kill, Does.Contain("StopAllCoroutines();"),
                "Killing a Queen must retire delayed minion spawns immediately.");
            Assert.That(kill.IndexOf("StopAllCoroutines();"), Is.LessThan(kill.IndexOf("base.Kill(")),
                "Queen delayed spawns must be cancelled before the wrapper enters shared Ship teardown/pooling.");
        }
        [Test]
        public void QueenSpawnedMinionsReceiveNoScriptedMovementOrders()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts", "Entities", "Ships", "Queen.cs"));

            int spawnStart = source.IndexOf("private void SpawnMinion(int shipIndex)");
            int killStart = source.IndexOf("public override void Kill(", spawnStart);
            Assert.That(spawnStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(killStart, Is.GreaterThan(spawnStart));

            string spawnMethod = source.Substring(spawnStart, killStart - spawnStart);
            Assert.That(spawnMethod, Does.Contain("ship.Transform.localPosition"),
                "Queen minions still need a physical launch position when instantiated.");
            Assert.That(spawnMethod, Does.Not.Contain("ship.MoveToPoint("),
                "Spawned Yellow Jackets must not receive a scripted movement destination before RL binds them.");
            Assert.That(spawnMethod, Does.Not.Contain("squad.Move("),
                "Queen spawning must not issue a scripted squad movement command.");
        }

    }
}