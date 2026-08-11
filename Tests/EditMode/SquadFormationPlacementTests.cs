using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadFormationPlacementTests
    {
        [Test]
        public void StartingPlacementPreservesWholeFormationThenFallsBackToNearestSlots()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.cs"));

            Assert.That(source, Does.Contain("private bool TryFindNearestFormationCenter(Vector2 requestedCenter, out Vector2 center)"));
            Assert.That(source, Does.Contain("private bool CanPlaceFormationAt(Vector2 center)"));
            Assert.That(source, Does.Contain("Level.Pathfinder.CanOccupyDestination(GetFormationSlot(ship, center), ship.GetClearance())"));
            Assert.That(source, Does.Contain("private void PlaceFormationSlotsIndividually(Vector2 requestedCenter)"));
            Assert.That(source, Does.Contain("FindNearestIndividualFormationSlot(ship, requestedSlot, placed)"));
            Assert.That(source, Does.Contain("WouldOverlapPlacedShip"));
            Assert.That(source, Does.Contain("TryFindNearestValidDestination(requestedSlot, ship.GetClearance(), out Vector2 validDestination)"));
            Assert.That(source, Does.Contain("if (TryFindNearestFormationCenter(position, out Vector2 formationCenter))"));
            Assert.That(source, Does.Contain("PlaceFormationSlotsIndividually(position);"));
        }
    }
}
