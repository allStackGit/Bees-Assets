using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MinionShipLoadedStateTests
    {
        [Test]
        public void ChildShipsDoNotClearParentFleetShipLoadedState()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("if (!ship.IsMinionShip && !ship.IsCarrierShip)", source);
            StringAssert.Contains("ship.FleetShip.IsLoadedIntoLevel = false;", source);
        }

        [Test]
        public void ChildRoleFlagsAreClearedOnlyAfterDeregistrationOwnershipCheck()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string source = File.ReadAllText(path);

            int ownershipIndex = source.IndexOf("if (!ship.IsMinionShip && !ship.IsCarrierShip)");
            int minionClearIndex = source.IndexOf("ship.IsMinionShip = false;", ownershipIndex);
            int carrierClearIndex = source.IndexOf("ship.IsCarrierShip = false;", ownershipIndex);

            Assert.That(ownershipIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(minionClearIndex, Is.GreaterThan(ownershipIndex));
            Assert.That(carrierClearIndex, Is.GreaterThan(ownershipIndex));
        }
    }
}
