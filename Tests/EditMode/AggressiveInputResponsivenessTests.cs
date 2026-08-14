using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class AggressiveInputResponsivenessTests
    {
        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            foreach (string part in pathParts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path);
        }

        [Test]
        public void AggressiveMovementSpreadsPathInitializationAcrossFrames()
        {
            string source = ReadSource("Scripts", "Levels", "Commands", "Aggressive.cs");

            Assert.That(source, Does.Contain("MoveTowardsEnemiesAcrossFrames"));
            Assert.That(source, Does.Contain("ship.MoveToTrackedPoint(target.GetPosition());"));
            Assert.That(source, Does.Contain("yield return null;"),
                "Attack startup must not initialize every ship path request in one Unity update.");
            Assert.That(source, Does.Contain("if (_moveTowardsEnemiesCoroutine == null && !IsDead)"),
                "Recurring aggressive timers must not start overlapping movement-startup coroutines.");
            Assert.That(source, Does.Not.Contain("                        MoveTowardsEnemies();"));
        }

        [Test]
        public void AggressiveTrackedMovementHonorsWorkerPathAndFailureOwnership()
        {
            string aggressive = ReadSource("Scripts", "Levels", "Commands", "Aggressive.cs");
            string tracked = ReadSource("Scripts", "Entities", "Ships", "Ship.TrackedMovement.cs");

            Assert.That(aggressive, Does.Contain("ship.MoveToTrackedPoint(target.GetPosition());"),
                "Aggressive must use the moving-target ownership policy instead of issuing fresh MoveToPoint orders every timer tick.");
            Assert.That(tracked, Does.Contain("if (IsPathfinding)"),
                "A live A* worker must retain ownership until it settles.");
            Assert.That(tracked, Does.Contain("if (_tryingToFindPathAgain)"),
                "Aggressive must not bypass the failed-path retry backoff while IsPathfinding is false.");
            Assert.That(tracked, Does.Contain("if (IsFollowingPath)"));
            Assert.That(tracked, Does.Contain("TrackedTargetPathReplanDistance = Pathfinder.Scale * 2f"),
                "A useful path should only be replaced after the tracked endpoint moves materially.");
            Assert.That(tracked, Does.Contain("TrackedTargetPathReplanInterval = 1f"),
                "Moving targets must have a bounded A* replan cadence.");
        }

        [Test]
        public void RecurringTrackedMovementDoesNotSupersedeLivePathSearches()
        {
            string tracked = ReadSource("Scripts", "Entities", "Ships", "Ship.TrackedMovement.cs");
            Assert.That(tracked, Does.Contain("if (IsPathfinding)"),
                "The shared tracked-target movement policy must preserve ownership of a live A* request.");
            Assert.That(tracked, Does.Contain("if (_tryingToFindPathAgain)"),
                "The shared tracked-target movement policy must preserve failed-search retry ownership.");

            string[] recurringMovementSources =
            {
                ReadSource("Scripts", "Levels", "Commands", "BombingRun.cs"),
                ReadSource("Scripts", "Levels", "Commands", "Charge.cs"),
                ReadSource("Scripts", "Levels", "Commands", "CircleSquad.cs"),
                ReadSource("Scripts", "Levels", "Commands", "ClosestFriendly.cs"),
                ReadSource("Scripts", "Levels", "Commands", "Guard.cs"),
                ReadSource("Scripts", "Levels", "Commands", "InAndOut.cs"),
                ReadSource("Scripts", "Levels", "Commands", "SwipeSquad.cs"),
                ReadSource("Scripts", "Levels", "Commands", "Heal.cs"),
                ReadSource("Scripts", "Levels", "Commands", "FullRetreat.cs"),
                ReadSource("Scripts", "Entities", "Ships", "Striker.cs")
            };

            foreach (string source in recurringMovementSources)
            {
                Assert.That(source,
                    Does.Contain("MoveToTrackedPoint(").Or.Contain("MoveTracked("),
                    "Recurring moving-target callers must route through the centralized tracked movement ownership policy.");
            }

            string shipMovement = ReadSource("Scripts", "Entities", "Ships", "Ship.Movement.cs").Replace("\r\n", "\n");
            Assert.That(shipMovement, Does.Contain("if (!IsPathfinding)\n                {\n                    MoveToPoint(FinalDestination);"),
                "Recurring collision-asteroid rechecks must let the current A* search settle before refreshing its destination.");
            Assert.That(shipMovement, Does.Contain("if (IsPathfinding && !foundObstacle && destination == PathfindingDestination)"),
                "Restating an identical timer-owned destination must not invalidate a live path worker whose result is still usable.");
        }
    }
}
