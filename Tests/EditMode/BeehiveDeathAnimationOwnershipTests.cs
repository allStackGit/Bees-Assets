using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class BeehiveDeathAnimationOwnershipTests
    {
        private static string Read(params string[] path) => File.ReadAllText(Path.Combine(Application.dataPath, Path.Combine(path)));

        [Test]
        public void ShipReleaseUsesSubclassLifecycleGate()
        {
            string shipPoolLifecycle = Read("Scripts", "Entities", "Ships", "Ship.PoolLifecycle.cs");
            string registry = Read("Scripts", "Levels", "GameState.Registry.cs");

            Assert.That(shipPoolLifecycle, Does.Contain("public virtual bool CanReturnToPool()"));
            Assert.That(shipPoolLifecycle, Does.Contain("return ProjectilesInFlight.Count == 0;"));
            Assert.That(registry, Does.Contain("ShipsToRelease.Where(ship => ship.CanReturnToPool())"));
        }

        [Test]
        public void BeehiveCannotBeReusedBeforeShrinkingAnimationCallbackCompletes()
        {
            string beehive = Read("Scripts", "Entities", "Ships", "Beehive.cs");
            string shrinking = Read("Scripts", "Entities", "Ships", "BeehiveShrinkingAnimation.cs");

            Assert.That(beehive, Does.Contain("private bool _isDeathAnimationPending;"));
            Assert.That(beehive, Does.Contain("_isDeathAnimationPending = true;"));
            Assert.That(beehive, Does.Contain("return !_isDeathAnimationPending && base.CanReturnToPool();"));
            Assert.That(beehive, Does.Contain("_isDeathAnimationPending = false;"));
            Assert.That(shrinking, Does.Contain("Beehive.FinalExplosion();"));
        }
    }
}
