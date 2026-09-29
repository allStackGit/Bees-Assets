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

            Assert.That(source, Does.Contain("private float BeaconClock => Stage != null && Stage.IsTraining ? Time.time : Time.realtimeSinceStartup;"),
                "Training must use scaled simulation time so accelerated workers do not wait wall-clock seconds.");
            Assert.That(source, Does.Contain("TimeSinceLastBeaconDropped = BeaconClock - ConfigData.MinimumDelayPerBeacon;"),
                "A freshly set up Scout must start with its first Beacon ready on whichever clock owns the current runtime.");
            Assert.That(source, Does.Contain("ChargingBar.Setup();"));
        }

        [Test]
        public void FireBargeRemainsReleaseQueueOwnedDuringPresentationDelay()
        {
            string fireBarge = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "FireBarge.cs"));
            string shipPoolLifecycle = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.PoolLifecycle.cs"));
            string registry = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));

            Assert.That(fireBarge, Does.Not.Contain("ShipsToRelease.Remove(this)"));
            Assert.That(fireBarge, Does.Contain("return !_waitingForDelayedRelease && base.CanReturnToPool();"));
            Assert.That(fireBarge, Does.Contain("public override void PrepareForLevelTeardown()"));
            Assert.That(shipPoolLifecycle, Does.Contain("public virtual void PrepareForLevelTeardown()"));
            Assert.That(registry, Does.Contain("ship.PrepareForLevelTeardown();"));
        }
    }
}
