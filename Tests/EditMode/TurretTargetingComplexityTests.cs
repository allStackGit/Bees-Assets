using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TurretTargetingComplexityTests
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
        public void ValidTargetScanDoesNotIndexDictionaryValuesWithElementAt()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");

            Assert.That(source, Does.Contain("foreach (Ship ship in ShipsWithinRange.Values)"));
            Assert.That(source, Does.Not.Contain("_shipsWithinRange.ElementAt"),
                "Dictionary.Values + ElementAt inside the physics-tick target loop makes the scan quadratic.");
        }

        [Test]
        public void ObstacleLinecastsUseWorldSpaceCoordinates()
        {
            string weapon = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Weapon.cs");
            string charge = ReadSource("Scripts", "Levels", "Commands", "Charge.cs");

            Assert.That(weapon, Does.Contain("(Vector2)PieceTransform.position"));
            Assert.That(weapon, Does.Contain("(Vector2)Ship.Transform.position"));
            Assert.That(weapon, Does.Contain("potentialTargetShip.Collider.ClosestPoint(origin)"));
            Assert.That(weapon, Does.Contain("Physics2D.Linecast(origin, targetPoint, ConfigData.ObstaclesLayerMask)"));
            Assert.That(charge, Does.Contain("barge.GetPosition() + levelOffset"));
            Assert.That(charge, Does.Contain("target.GetPosition() + levelOffset"));
        }
    }
}
