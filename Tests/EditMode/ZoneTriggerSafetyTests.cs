using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ZoneTriggerSafetyTests
    {
        [Test]
        public void ZoneIgnoresNonShipOrDeadTriggerOccupants()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Zone.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("if (ship == null || ship.IsDead)", source);
            StringAssert.Contains("if (Ships.Add(ship))", source);
            StringAssert.Contains("OnShipEnter?.Invoke(ship);", source);
        }
    }
}
