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
        public void Uranus1HasNoDelayedCruiserSpawnToOutliveLevelTeardown()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Uranus1.cs"));
            int ending = source.IndexOf("WinningSide = State.IsSideKilled", StringComparison.Ordinal);
            int close = source.IndexOf("CloseLevel();", ending, StringComparison.Ordinal);

            Assert.That(ending, Is.GreaterThanOrEqualTo(0));
            Assert.That(close, Is.GreaterThan(ending));
            Assert.That(source, Does.Not.Contain("cruiserTimer"),
                "The struck Cruiser/Fritz sequence must not leave a delayed spawn timer behind after the mission was removed.");
        }
    }
}
