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
        public void StartingPlacementMovesFormationAsAUnitBeforeChangingOffsets()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.cs"));

            Assert.That(source, Does.Contain("private Vector2 FindNearestFormationCenter(Vector2 requestedCenter)"));
            Assert.That(source, Does.Contain("private bool CanPlaceFormationAt(Vector2 center)"));
            Assert.That(source, Does.Contain("Level.Pathfinder.CanOccupyDestination(shipPosition, ship.GetClearance())"));
            Assert.That(source, Does.Contain("position = FindNearestFormationCenter(position);"));
            Assert.That(source, Does.Contain("_adjustment = GetFormationAdjustment(ship);"));
            Assert.That(source, Does.Contain("ship.transform.localPosition = new Vector2(position.x + _adjustment.x, position.y + _adjustment.y);"));
        }
    }
}
