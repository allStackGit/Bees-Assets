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
            string bombingRun = ReadSource("Scripts", "Levels", "Commands", "BombingRun.cs");
            string charge = ReadSource("Scripts", "Levels", "Commands", "Charge.cs");
            string circle = ReadSource("Scripts", "Levels", "Commands", "CircleSquad.cs");
            string closestFriendly = ReadSource("Scripts", "Levels", "Commands", "ClosestFriendly.cs");
            string guard = ReadSource("Scripts", "Levels", "Commands", "Guard.cs");
            string inAndOut = ReadSource("Scripts", "Levels", "Commands", "InAndOut.cs");
            string swipe = ReadSource("Scripts", "Levels", "Commands", "SwipeSquad.cs");
            string heal = ReadSource("Scripts", "Levels", "Commands", "Heal.cs");
            string retreat = ReadSource("Scripts", "Levels", "Commands", "FullRetreat.cs");
            string striker = ReadSource("Scripts", "Entities", "Ships", "Striker.cs");
            string shipMovement = ReadSource("Scripts", "Entities", "Ships", "Ship.Movement.cs").Replace("\r\n", "\n");

            Assert.That(bombingRun, Does.Contain("if (!ship.IsPathfinding)"),
                "Bombing Run can refresh a tracked target every 0.25 seconds and must not invalidate a live A* request.");
            Assert.That(charge, Does.Contain("if (!ship.IsPathfinding)"),
                "Charge pursuit must let its current A* request settle before refreshing a target position.");
            Assert.That(circle, Does.Contain("!GetSquad().GetShips().Any(ship => ship.IsPathfinding)"),
                "Circle Squad refreshes a moving enemy-relative destination as often as every 0.25 seconds and must not churn path workers.");
            Assert.That(closestFriendly, Does.Contain("!GetSquad().GetShips().Any(ship => ship.IsPathfinding)"),
                "Following a moving friendly squad must not supersede a live path search on every timer tick.");
            Assert.That(guard, Does.Contain("!GetSquad().GetShips().Any(ship => ship.IsPathfinding)"),
                "Guard formation tracking must wait for live path searches before refreshing its moving destination.");
            Assert.That(inAndOut, Does.Contain("!GetSquad().GetShips().Any(ship => ship.IsPathfinding)"),
                "In-and-Out must not run the shared unguarded pursuit loop while any squad path request is live.");
            Assert.That(swipe, Does.Contain("!GetSquad().GetShips().Any(ship => ship.IsPathfinding)"),
                "Swipe pursuit must not run the shared unguarded pursuit loop while any squad path request is live.");
            Assert.That(heal, Does.Contain("if (!_ship.IsPathfinding)"),
                "Healing movement tracks ship-based Beehive destinations and must not churn path workers.");
            Assert.That(retreat, Does.Contain("_shipIdsWarping.Contains(ship.Id) && !ship.IsPathfinding"),
                "Retreat's recurring Warp Gate refresh must not churn active path workers.");
            Assert.That(striker, Does.Contain("else if (!IsPathfinding)"),
                "A returning Striker tracks a moving Carrier and must not supersede its current path request.");
            Assert.That(shipMovement, Does.Contain("if (!IsPathfinding)\n                {\n                    MoveToPoint(FinalDestination);"),
                "Recurring collision-asteroid rechecks must let the current A* search settle before refreshing its destination.");
            Assert.That(shipMovement, Does.Contain("if (IsPathfinding && !foundObstacle && destination == PathfindingDestination)"),
                "Restating an identical timer-owned destination must not invalidate a live path worker whose result is still usable.");
        }
    }
}
