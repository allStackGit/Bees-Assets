using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CarrierSquadLifecycleTests
    {
        [Test]
        public void CarrierSquadSetsDroneTypeBeforeChoosingShipCount()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "CarrierSquad.cs");
            string source = File.ReadAllText(path);

            int typeAssignment = source.IndexOf("IsDroneSquad = CarrierSquadType == ConfigData.ShipTypes.Drone;");
            int setupShips = source.IndexOf("SetupShips();", typeAssignment);
            Assert.That(typeAssignment, Is.GreaterThanOrEqualTo(0));
            Assert.That(setupShips, Is.GreaterThan(typeAssignment));

            StringAssert.Contains("IsDroneSquad ? ConfigData.Configuration.CarrierCarryDroneMax : ConfigData.Configuration.CarrierCarryStrikerMax", source);
        }
    }
}