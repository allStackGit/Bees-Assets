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
        public void ObstacleLinecastsConvertLevelLocalPositionsToWorldSpace()
        {
            string turret = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");
            string charge = ReadSource("Scripts", "Levels", "Commands", "Charge.cs");

            Assert.That(turret, Does.Contain("GetPosition() + Level.GetPosition()"));
            Assert.That(turret, Does.Contain("GetTargetPoint(potentialTargetShip) + Level.GetPosition()"));
            Assert.That(charge, Does.Contain("barge.GetPosition() + levelOffset"));
            Assert.That(charge, Does.Contain("barge.Charge.TargetShip.GetPosition() + levelOffset"));
        }
    }
}
