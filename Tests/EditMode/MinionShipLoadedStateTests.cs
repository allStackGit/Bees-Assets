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
        public void ChildShipsDoNotOwnFleetShipLoadedState()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("if (!ship.IsMinionShip && !ship.IsCarrierShip)", source);
            StringAssert.Contains("ship.FleetShip.IsLoadedIntoLevel = true;", source);
            StringAssert.Contains("ship.FleetShip.IsLoadedIntoLevel = false;", source);
        }

        [Test]
        public void ChildRoleFlagsAreClearedOnlyAfterDeregistrationOwnershipCheck()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string source = File.ReadAllText(path);

            int ownershipIndex = source.LastIndexOf("if (!ship.IsMinionShip && !ship.IsCarrierShip)");
            int minionClearIndex = source.IndexOf("ship.IsMinionShip = false;", ownershipIndex);
            int carrierClearIndex = source.IndexOf("ship.IsCarrierShip = false;", ownershipIndex);

            Assert.That(ownershipIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(minionClearIndex, Is.GreaterThan(ownershipIndex));
            Assert.That(carrierClearIndex, Is.GreaterThan(ownershipIndex));
        }

        [TestCase("Queen.cs", "ship.IsMinionShip = true;", "ship.Setup(")]
        [TestCase("Scout.cs", "ship.IsMinionShip = true;", "ship.Setup(")]
        public void SpawnedMinionsDeclareRoleBeforeSetup(string filename, string roleStatement, string setupStatement)
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", filename);
            string source = File.ReadAllText(path);

            int roleIndex = source.IndexOf(roleStatement);
            int setupIndex = source.IndexOf(setupStatement, roleIndex);
            Assert.That(roleIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(setupIndex, Is.GreaterThan(roleIndex));
        }

        [Test]
        public void CarrierChildrenDeclareRoleBeforeSetup()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "CarrierSquad.cs");
            string source = File.ReadAllText(path);

            int roleIndex = source.IndexOf("_ship.IsCarrierShip = true;");
            int setupIndex = source.IndexOf("_ship.Setup(", roleIndex);
            Assert.That(roleIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(setupIndex, Is.GreaterThan(roleIndex));
        }
    }
}
