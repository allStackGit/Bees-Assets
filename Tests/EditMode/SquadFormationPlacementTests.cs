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
        public void StartingPlacementPreservesAuthoredOffsetsAndSearchesWholeMapBeforeDistorting()
        {
            string squadSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.cs"));
            string movementSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.Movement.cs"));

            Assert.That(squadSource, Does.Contain("_preserveAuthoredOffsetsOnNextSetOffsets = true;"));
            Assert.That(movementSource, Does.Contain("if (_preserveAuthoredOffsetsOnNextSetOffsets)"));
            Assert.That(movementSource, Does.Contain("_preserveAuthoredOffsetsOnNextSetOffsets = false;"));
            Assert.That(squadSource, Does.Contain("int maxSearchDistance = Mathf.Max(Level.MapWidth, Level.MapHeight);"),
                "An intact authored formation should be searched for across the usable map before per-ship fallback is allowed.");
            Assert.That(squadSource, Does.Contain("private bool TryFindNearestFormationCenter(Vector2 requestedCenter, out Vector2 center)"));
            Assert.That(squadSource, Does.Contain("Level.Pathfinder.CanOccupyDestination(GetFormationSlot(ship, center), ship.GetClearance())"));
            Assert.That(squadSource, Does.Contain("private void PlaceFormationSlotsIndividually(Vector2 requestedCenter)"));
            Assert.That(squadSource, Does.Contain("if (TryFindNearestFormationCenter(position, out Vector2 formationCenter))"));
            Assert.That(squadSource, Does.Contain("PlaceFormationSlotsIndividually(position);"));
        }
    }
}
