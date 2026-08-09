using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class FireBargeDeathAccountingTests
    {
        [Test]
        public void FireBargeUsesSharedKilledStatsWithoutManualDuplicateLoss()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "FireBarge.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("LogKilledStats();", source);
            StringAssert.DoesNotContain("Squad.SavedSquad.Stats.ShipsLost++", source);
            StringAssert.DoesNotContain("FleetShip.IsDead = true", source);
        }
    }
}
