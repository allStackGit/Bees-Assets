using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ShootingStrategyQueueTimingTests
    {
        [TestCase("Aggressive.cs")]
        [TestCase("InAndOut.cs")]
        [TestCase("SwipeSquad.cs")]
        [TestCase("Charge.cs")]
        public void EnemyMovementQueueIsDiscardedAfterCurrentShootingStrategyIsApplied(string filename)
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", filename);
            string source = File.ReadAllText(path);

            int baseExecute = source.IndexOf("base.Execute(", StringComparison.Ordinal);
            int clearOriginal = source.IndexOf("OriginalQueue.Clear();", baseExecute, StringComparison.Ordinal);
            int clearWorking = source.IndexOf("TargetingQueue.Clear();", clearOriginal, StringComparison.Ordinal);

            Assert.That(baseExecute, Is.GreaterThanOrEqualTo(0), $"{filename} must call base.Execute().");
            Assert.That(clearOriginal, Is.GreaterThan(baseExecute),
                $"{filename} must discard the queue built before base.Execute() installs the server-selected shooting strategy.");
            Assert.That(clearWorking, Is.GreaterThan(clearOriginal),
                $"{filename} must discard both copies of the stale movement-target queue.");
        }
    }
}
