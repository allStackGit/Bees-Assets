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
        public void TurretTargetsFirstLiveNearbyAsteroid()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");
            string source = File.ReadAllText(path);
            StringAssert.Contains("FirstOrDefault(asteroid => asteroid != null && !asteroid.IsDead)", source);
        }
    }
}
