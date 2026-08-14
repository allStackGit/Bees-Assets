using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MovementStaleStateTests
    {
        [Test]
        public void AsteroidDoubleCheckPrunesDeadEntries()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Movement.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public void NearbyAsteroidDoubleCheck()");
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            string method = source.Substring(start, source.IndexOf("public void LeftNearbyAsteroid", start) - start);
            StringAssert.Contains("NearbyAsteroids.RemoveAll(asteroid => asteroid == null || asteroid.IsDead)", method);
        }

        [Test]
        public void ExplicitStopCancelsPendingPathRetry()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Movement.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public void StopMoving(");
            int end = source.IndexOf("public void ClearPreviousDesintation", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string method = source.Substring(start, end - start);
            StringAssert.Contains("CancelTimer(_tryToFindPathAgainTimer)", method);
            StringAssert.Contains("_tryingToFindPathAgain = false", method);
        }

        [Test]
        public void TurretTargetsFirstLiveNearbyAsteroidWithoutLinq()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");
            string source = File.ReadAllText(path);
            StringAssert.Contains("for (int i = 0; i < Ship.NearbyAsteroids.Count; i++)", source);
            StringAssert.Contains("if (asteroid != null && !asteroid.IsDead)", source);
            StringAssert.DoesNotContain("FirstOrDefault(asteroid =>", source);
        }

        [Test]
        public void TrackedSquadMovementPreservesFormationPlacement()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Movement.cs");
            string source = File.ReadAllText(path);
            StringAssert.Contains("public void MoveTracked(Vector2 destination)", source);
            StringAssert.Contains("MoveInternal(destination, true);", source);
            StringAssert.Contains("ship.MoveToTrackedPoint(shipDestination);", source);
            StringAssert.Contains("ship.MoveToPoint(shipDestination);", source);
        }

        [Test]
        public void RecurringFollowerCommandsUseTrackedMovement()
        {
            string commandsPath = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands");
            string shipsPath = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships");

            string guard = File.ReadAllText(Path.Combine(commandsPath, "Guard.cs"));
            string closestFriendly = File.ReadAllText(Path.Combine(commandsPath, "ClosestFriendly.cs"));
            string inAndOut = File.ReadAllText(Path.Combine(commandsPath, "InAndOut.cs"));
            string swipe = File.ReadAllText(Path.Combine(commandsPath, "SwipeSquad.cs"));
            string bombingRun = File.ReadAllText(Path.Combine(commandsPath, "BombingRun.cs"));
            string fullRetreat = File.ReadAllText(Path.Combine(commandsPath, "FullRetreat.cs"));
            string striker = File.ReadAllText(Path.Combine(shipsPath, "Striker.cs"));

            StringAssert.Contains("MoveTracked(GetDestination())", guard);
            StringAssert.Contains("MoveTracked(GetDestination())", closestFriendly);
            StringAssert.Contains("ship.MoveToTrackedPoint(target.GetPosition())", inAndOut);
            StringAssert.Contains("ship.MoveToTrackedPoint(target.GetPosition())", swipe);
            StringAssert.Contains("ship.MoveToTrackedPoint(ship.TargetEnemyShipToFollow.GetPosition())", bombingRun);
            StringAssert.Contains("ship.MoveToTrackedPoint(_f_targetPosition)", fullRetreat);
            StringAssert.Contains("MoveToTrackedPoint(_destination)", striker);
        }
    }
}
