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
    }
}