using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SavedSquadDeletionTests
    {
        [Test]
        public void DeletingSavedSquadReleasesFleetShipMembership()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Data", "SavedSquadsData.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public void RemoveSquadFromList(SavedSquad squad)", source);
            StringAssert.Contains("foreach (SquadShip squadShip in squad.GetSquadShips())", source);
            StringAssert.Contains("fleetShip.DoesBelongToSavedSquad = false;", source);
            StringAssert.Contains("_savedSquadsList.Remove(squad);", source);
        }
    }
}
