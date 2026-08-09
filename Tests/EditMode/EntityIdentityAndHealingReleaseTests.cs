using System.IO;
using Assets.Scripts.Entities;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class EntityIdentityAndHealingReleaseTests
    {
        [Test]
        public void DestroyedEntityPreservesUnityNullSemantics()
        {
            GameObject go = new GameObject("entity-null-semantics");
            Entity entity = go.AddComponent<Entity>();
            entity.Id = 42;

            Object.DestroyImmediate(go);

            Assert.That(entity == null, Is.True);
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
