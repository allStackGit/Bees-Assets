using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RetreatCommandLifecycleTests
    {
        [Test]
        public void AlreadySafeRetreatStopsMovementBeforeDelayedFinalization()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Retreat.cs");
            string source = File.ReadAllText(path);

            int stopIndex = source.IndexOf("GetSquad().StopMoving();");
            int delayIndex = source.IndexOf("_delayedSetFinalizeTimer.Reuse(3f, DelaySetFinalize);");

            Assert.That(stopIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(delayIndex, Is.GreaterThan(stopIndex));
        }
    }
}
