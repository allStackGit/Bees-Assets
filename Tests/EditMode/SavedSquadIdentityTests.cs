using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SavedSquadIdentityTests
    {
        [Test]
        public void UnsavedCopyRetainsTheNewFleetShipInsteadOfOnlyItsNegativeId()
        {
            string savedSquadPath = Path.Combine(Application.dataPath, "Scripts", "Data", "SavedSquad.cs");
            string savedSquadSource = File.ReadAllText(savedSquadPath);
            string squadShipPath = Path.Combine(Application.dataPath, "Scripts", "Data", "SquadShip.cs");
            string squadShipSource = File.ReadAllText(squadShipPath);

            StringAssert.Contains("new SquadShip(newFleetShip, squadShip.Offset)", savedSquadSource);
            StringAssert.Contains("public SquadShip(FleetShip fleetShip, Vector2 offset)", squadShipSource);
            StringAssert.Contains("CachedFleetShip = fleetShip;", squadShipSource);
            StringAssert.Contains("_hasCachedFleetShip = true;", squadShipSource);
        }

        [Test]
        public void SavedSquadTypedEqualityComparesSavedSquads()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Data", "SavedSquad.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public bool Equals(SavedSquad other)", source);
            StringAssert.DoesNotContain("public bool Equals(FleetShip other)", source);
        }
    }
}
