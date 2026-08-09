using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class EntityIdentityAndHealingReleaseTests
    {
        [Test]
        public void EntityEqualityPreservesUnityNullSemantics()
        {
            string entityPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Entity.cs");
            string source = File.ReadAllText(entityPath);

            StringAssert.Contains("(UnityEngine.Object)this == null", source);
            StringAssert.Contains("(UnityEngine.Object)other == null", source);
            StringAssert.Contains("(UnityEngine.Object)a == null", source);
            StringAssert.Contains("(UnityEngine.Object)b == null", source);
        }

        [Test]
        public void ShipRemovalReleasesHealingBeforeRuntimeIdentityLeavesRegistry()
        {
            string registryPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string registry = File.ReadAllText(registryPath);

            int methodStart = registry.IndexOf("public void RemoveShip(Ship ship)");
            int methodEnd = registry.IndexOf("public void AddDeadBody", methodStart);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = registry.Substring(methodStart, methodEnd - methodStart);
            int release = method.IndexOf("healCommand.ShipBecameUnavailable(ship)");
            int removeId = method.IndexOf("ShipsById.Remove(ship.Id)");
            int queueRelease = method.IndexOf("ShipsToRelease.Add(ship)");

            Assert.That(release, Is.GreaterThanOrEqualTo(0));
            Assert.That(removeId, Is.GreaterThan(release));
            Assert.That(queueRelease, Is.GreaterThan(removeId));
        }

        [Test]
        public void HealUnavailableHookReleasesReservationImmediately()
        {
            string healPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Heal.cs");
            string source = File.ReadAllText(healPath);

            int hookStart = source.IndexOf("public void ShipBecameUnavailable(Ship ship)");
            int releaseStart = source.IndexOf("private void ReleaseHealingReservation", hookStart);
            Assert.That(hookStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(releaseStart, Is.GreaterThan(hookStart));

            string hook = source.Substring(hookStart, releaseStart - hookStart);
            StringAssert.Contains("ReleaseHealingReservation(ship);", hook);
            StringAssert.Contains("FinalizeIfAssignedShipsAreDone();", hook);
        }
    }
}
