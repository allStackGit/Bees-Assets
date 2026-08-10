using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class Pluto4WaveTeardownTests
    {
        [Test]
        public void ObjectiveEndCancelsAllPendingWavesBeforeClosingLevel()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Pluto4.cs"));
            int objectiveEnd = source.IndexOf("_questPoints = personnelEvacuated;", StringComparison.Ordinal);
            int cancelClock = source.IndexOf("CancelTimer(clock);", objectiveEnd, StringComparison.Ordinal);
            int cancelWave2 = source.IndexOf("CancelTimer(wave2);", objectiveEnd, StringComparison.Ordinal);
            int cancelWave3 = source.IndexOf("CancelTimer(wave3);", objectiveEnd, StringComparison.Ordinal);
            int cancelWave4 = source.IndexOf("CancelTimer(wave4);", objectiveEnd, StringComparison.Ordinal);
            int closeLevel = source.IndexOf("CloseLevel();", objectiveEnd, StringComparison.Ordinal);

            Assert.That(cancelClock, Is.GreaterThan(objectiveEnd));
            Assert.That(cancelWave2, Is.GreaterThan(cancelClock));
            Assert.That(cancelWave3, Is.GreaterThan(cancelWave2));
            Assert.That(cancelWave4, Is.GreaterThan(cancelWave3));
            Assert.That(closeLevel, Is.GreaterThan(cancelWave4));
        }
    }
}
