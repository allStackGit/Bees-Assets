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
        public void CommonTimerTeardownCancelsAllShipOwnedLevelTimers()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string source = File.ReadAllText(path);

            int helperStart = source.IndexOf("protected void CancelOwnedTimers()", System.StringComparison.Ordinal);
            int killStart = source.IndexOf("public virtual void Kill(", helperStart, System.StringComparison.Ordinal);
            Assert.That(helperStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(killStart, Is.GreaterThan(helperStart));
            string helper = source.Substring(helperStart, killStart - helperStart);

            StringAssert.Contains("CancelTimer(_asteroidDoubleCheckTimer)", helper);
            StringAssert.Contains("CancelTimer(_tryToFindPathAgainTimer)", helper);
            StringAssert.Contains("CancelTimer(_combatTimerScaledTimer)", helper);
            StringAssert.Contains("CancelTimer(_showShipStatsTimer)", helper);
            StringAssert.Contains("Weapons.ForEach(weapon => weapon.CancelTimer())", helper);

            int targetStart = source.IndexOf("public Ship SetAndGetTargetEnemy", killStart, System.StringComparison.Ordinal);
            string kill = source.Substring(killStart, targetStart - killStart);
            StringAssert.Contains("CancelOwnedTimers();", kill);
        }

        [Test]
        public void FireBargeSpecialDeathUsesCommonTimerTeardown()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "FireBarge.cs");
            string source = File.ReadAllText(path);

            int killStart = source.IndexOf("public override void Kill(", System.StringComparison.Ordinal);
            int canReturnStart = source.IndexOf("public override bool CanReturnToPool()", killStart, System.StringComparison.Ordinal);
            Assert.That(killStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(canReturnStart, Is.GreaterThan(killStart));
            string kill = source.Substring(killStart, canReturnStart - killStart);

            StringAssert.Contains("CancelOwnedTimers();", kill);
        }
    }
}
