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

        [Test]
        public void StrikerBombingRunWaitsForCarrierReturnBeforeFinalizing()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "BombingRun.cs");
            string source = File.ReadAllText(path);

            int endBombingRun = source.IndexOf("private void EndBombingRun()");
            int waitForReturn = source.IndexOf("if (!HaveAllShipsFinished(_endBombingRun_ships))", endBombingRun);
            int earlyReturn = source.IndexOf("return;", waitForReturn);
            int finalize = source.IndexOf("SetFinalize(\"Completed bombing run\")", endBombingRun);

            Assert.That(endBombingRun, Is.GreaterThanOrEqualTo(0));
            Assert.That(waitForReturn, Is.GreaterThan(endBombingRun));
            Assert.That(earlyReturn, Is.GreaterThan(waitForReturn));
            Assert.That(finalize, Is.GreaterThan(earlyReturn));
        }
    }
}