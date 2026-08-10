using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class Uranus3EndingTimerTests
    {
        [Test]
        public void EndingAlwaysCancelsPendingReinforcementsBeforeClosingLevel()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Uranus3.cs"));
            int ending = source.IndexOf("bool isBargeSquadDead = bargeSquad.IsDead;", StringComparison.Ordinal);
            int cancel = source.IndexOf("CancelTimer(reinforcements);", ending, StringComparison.Ordinal);
            int close = source.IndexOf("CloseLevel();", ending, StringComparison.Ordinal);
            int branch = source.IndexOf("if (isBargeSquadDead)", ending, StringComparison.Ordinal);

            Assert.That(ending, Is.GreaterThanOrEqualTo(0));
            Assert.That(cancel, Is.GreaterThan(ending));
            Assert.That(close, Is.GreaterThan(cancel));
            Assert.That(branch, Is.GreaterThan(close));
        }
    }
}
