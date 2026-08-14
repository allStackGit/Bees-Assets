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

            int methodStart = source.IndexOf("private void RecordSighting(");
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));

            int addVisibility = source.IndexOf("RecordHiveMindSighting(Ship, _shipEnter)", methodStart);
            int commandEligibility = source.IndexOf("!rewardSquad.HasCommand", methodStart);
            Assert.That(addVisibility, Is.GreaterThan(methodStart));
            Assert.That(commandEligibility, Is.GreaterThan(addVisibility));
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
    }
}
