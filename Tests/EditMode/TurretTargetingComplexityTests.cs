using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TurretTargetingComplexityTests
    {
        [Test]
        public void ValidTargetScanDoesNotIndexDictionaryValuesWithElementAt()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs"));

            Assert.That(source, Does.Contain("foreach (Ship ship in ShipsWithinRange.Values)"));
            Assert.That(source, Does.Not.Contain("_shipsWithinRange.ElementAt"),
                "Dictionary.Values + ElementAt inside the physics-tick target loop makes the scan quadratic.");
        }
    }
}
