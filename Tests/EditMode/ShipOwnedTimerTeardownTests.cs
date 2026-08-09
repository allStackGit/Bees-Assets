using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ShipOwnedTimerTeardownTests
    {
        [Test]
        public void KillCancelsAllShipOwnedLevelTimers()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string source = File.ReadAllText(path);

            int killStart = source.IndexOf("public virtual void Kill(");
            int targetStart = source.IndexOf("public Ship SetAndGetTargetEnemy", killStart);
            Assert.That(killStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(targetStart, Is.GreaterThan(killStart));
            string kill = source.Substring(killStart, targetStart - killStart);

            StringAssert.Contains("CancelTimer(_asteroidDoubleCheckTimer)", kill);
            StringAssert.Contains("CancelTimer(_tryToFindPathAgainTimer)", kill);
            StringAssert.Contains("CancelTimer(_combatTimerScaledTimer)", kill);
            StringAssert.Contains("CancelTimer(_showShipStatsTimer)", kill);
        }
    }
}
