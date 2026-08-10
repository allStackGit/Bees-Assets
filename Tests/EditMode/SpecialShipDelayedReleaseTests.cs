using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SpecialShipDelayedReleaseTests
    {
        [Test]
        public void FreshScoutStartsBeaconCooldownAtReadyPoint()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Scout.cs"));

            Assert.That(source, Does.Contain("TimeSinceLastBeaconDropped = Time.realtimeSinceStartup - ConfigData.MinimumDelayPerBeacon;"));
            Assert.That(source, Does.Contain("ChargingBar.Setup();"));
        }

        [Test]
        public void FireBargeRemainsReleaseQueueOwnedDuringPresentationDelay()
        {
            string fireBarge = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "FireBarge.cs"));
            string registry = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));

            Assert.That(fireBarge, Does.Not.Contain("ShipsToRelease.Remove(this)"));
            Assert.That(fireBarge, Does.Contain("return !_waitingForDelayedRelease && base.CanReturnToPool();"));
            Assert.That(fireBarge, Does.Contain("PrepareForLevelTeardown()"));
            Assert.That(registry, Does.Contain("fireBarge.PrepareForLevelTeardown();"));
        }
    }
}
