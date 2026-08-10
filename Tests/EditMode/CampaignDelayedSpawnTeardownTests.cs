using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignDelayedSpawnTeardownTests
    {
        [Test]
        public void Uranus1CancelsCruiserSpawnBeforeClosingLevel()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Uranus1.cs"));
            int ending = source.IndexOf("WinningSide = State.IsSideKilled", StringComparison.Ordinal);
            int cancel = source.IndexOf("CancelTimer(cruiserTimer);", ending, StringComparison.Ordinal);
            int close = source.IndexOf("CloseLevel();", ending, StringComparison.Ordinal);

            Assert.That(cancel, Is.GreaterThan(ending));
            Assert.That(close, Is.GreaterThan(cancel));
        }
    }
}
