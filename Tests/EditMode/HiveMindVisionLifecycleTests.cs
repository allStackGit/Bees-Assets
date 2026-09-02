using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class HiveMindVisionLifecycleTests
    {
        [Test]
        public void SightingIsRecordedBeforeCommandRewardEligibilityIsChecked()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "HivemindVision.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf("private void RecordShipSighting()");
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));

            int addVisibility = source.IndexOf("state.RecordHiveMindSighting(Ship, _shipEnter)", methodStart);
            int rewardSquad = source.IndexOf("Squad rewardSquad", methodStart);
            int commandEligibility = source.IndexOf("!rewardSquad.HasCommand", methodStart);
            Assert.That(addVisibility, Is.GreaterThan(methodStart));
            Assert.That(rewardSquad, Is.GreaterThan(addVisibility));
            Assert.That(commandEligibility, Is.GreaterThan(rewardSquad));
        }

        [Test]
        public void SightingRejectsDeadFriendlyAndNonShipContacts()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "HivemindVision.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("_shipEnter == null", source);
            StringAssert.Contains("_shipEnter.IsDead", source);
            StringAssert.Contains("_shipEnter.Side == Ship.Side", source);
        }

        [Test]
        public void ShipRegistrationOwnsExactlyOneVisibilitySetCreationPath()
        {
            string lifecycle = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Lifecycle.cs"));
            string registry = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));

            StringAssert.DoesNotContain("HivemindShips[Side - 1].Add(Id, new HashSet<Ship>());", lifecycle);
            StringAssert.Contains("HivemindShips[ship.Side - 1][ship.Id] =", registry);
            StringAssert.Contains("new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance)", registry);
        }

        [Test]
        public void ObserverDeathDoesNotEraseFactionWideLearnedSightings()
        {
            string registry = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));
            string queries = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.Queries.cs"));

            StringAssert.Contains("observerVisibility.Add(spotted);", queries);
            StringAssert.Contains("return VisionCache[sideIndex].Add(spotted);", queries);
            StringAssert.Contains("observerMap.Remove(ship.Id);", registry,
                "The dead observer's attribution set should be retired.");
            StringAssert.DoesNotContain("removedObserverSideIndex", registry,
                "Observer removal must not use observer ownership to recompute persistent Hive Mind knowledge.");
            StringAssert.DoesNotContain("sideCache.Clear()", registry,
                "Once the Hive Mind has seen a live target, observer death must not make the faction forget it.");
        }
    }
}
