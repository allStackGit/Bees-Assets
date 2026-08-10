using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TitaniaCampaignTransitionTests
    {
        [Test]
        public void BeenocularsEndingRestoresPlanetIntermissionStateBeforeAdvancing()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Titania2Beenoculars.cs"));

            int ending = source.IndexOf("public void Titania2CampaignEnding()");
            Assert.That(ending, Is.GreaterThanOrEqualTo(0));
            string endingSource = source.Substring(ending);

            int resetIntro = endingSource.IndexOf("ConfigData.HasSeenPreLevelIntro = false;");
            int resetIntermission = endingSource.IndexOf("ConfigData.HasSeenIntermission = false;");
            int advance = endingSource.IndexOf("ConfigData.UserProgressData.AdvanceToNextLevel();");

            Assert.That(resetIntro, Is.GreaterThanOrEqualTo(0));
            Assert.That(resetIntermission, Is.GreaterThanOrEqualTo(0));
            Assert.That(advance, Is.GreaterThan(resetIntro));
            Assert.That(advance, Is.GreaterThan(resetIntermission));
        }
    }
}
