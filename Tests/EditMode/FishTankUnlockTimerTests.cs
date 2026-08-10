using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class FishTankUnlockTimerTests
    {
        [Test]
        public void FishTankUnlockDoesNotRepeatAfterThirtyMinutes()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs");
            string source = File.ReadAllText(path);
            int methodIndex = source.IndexOf("public void FishTankTrigger()");
            int timerIndex = source.IndexOf("_fishTank.Reuse(60 * 30f", methodIndex);
            int addTimerIndex = source.IndexOf("AddTimer(_fishTank);", timerIndex);
            string timerSetup = source.Substring(timerIndex, addTimerIndex - timerIndex);

            Assert.That(methodIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(timerIndex, Is.GreaterThan(methodIndex));
            StringAssert.DoesNotContain("}, true);", timerSetup);
        }
    }
}
