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
        public void DeletingSavedSquadRefillsCasualtySlotsBeforeReleasingShipsToFleet()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Data", "SavedSquadsData.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public void RemoveSquadFromList(SavedSquad squad)", source);
            StringAssert.Contains("storedSquad.Id >= 0 && storedSquad.HasBeenSavedToStorage", source);
            StringAssert.Contains("TryFillCasualtySlot(releasedShip, storedSquad.Side)", source);
            StringAssert.Contains("destinationSquad.GetDeadShips()", source);
            StringAssert.Contains("deadShip.ShipType == releasedShip.Type", source);
            StringAssert.Contains("Vector2 preservedOffset = casualtySlot.Offset;", source);
            StringAssert.Contains("destinationSquad.RemoveShipFromSquad(casualtySlot, false);", source);
            StringAssert.Contains("destinationSquad.AddShipToSquad(new SquadShip(releasedShip, preservedOffset));", source);
            StringAssert.Contains("releasedShip.DoesBelongToSavedSquad = false;", source);
        }

        [Test]
        public void NegativeIdEncounterSquadsDoNotRefillPersistentCasualtySlots()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Data", "SavedSquadsData.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("bool refillCasualtySlots = storedSquad.Id >= 0", source);
        }
    }
}
