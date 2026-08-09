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

        [Test]
        public void HealReusesFreedBeehiveSlotsBeforeFinalizing()
        {
            string healPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Heal.cs");
            string source = File.ReadAllText(healPath);

            int assignMethod = source.IndexOf("private void AssignAvailableHealingSlots()");
            int finalizeMethod = source.IndexOf("private void FinalizeIfAssignedShipsAreDone()");
            int moveMethod = source.IndexOf("public void MoveToBeehives()");
            Assert.That(assignMethod, Is.GreaterThanOrEqualTo(0));
            Assert.That(finalizeMethod, Is.GreaterThan(assignMethod));
            Assert.That(moveMethod, Is.GreaterThan(finalizeMethod));

            string assign = source.Substring(assignMethod, finalizeMethod - assignMethod);
            string finalize = source.Substring(finalizeMethod, moveMethod - finalizeMethod);

            StringAssert.Contains("_shipsThatNeedBeehive.Dequeue()", assign);
            StringAssert.Contains("_shipsAndBeehives[_ship.Id] = _beehive;", assign);
            int reassign = finalize.IndexOf("AssignAvailableHealingSlots();");
            int completionCheck = finalize.IndexOf("ShipsWaitingToHeal.Count == 0");
            Assert.That(reassign, Is.GreaterThanOrEqualTo(0));
            Assert.That(completionCheck, Is.GreaterThan(reassign));
            StringAssert.Contains("_shipsThatNeedBeehive == null || _shipsThatNeedBeehive.Count == 0", finalize);
        }
    }
}
