using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignNarrativeResetTests
    {
        [Test]
        public void CampaignMissionZeroClearsPriorNarrativeTransitionState()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "ConfigData.Campaign.cs"));

            int caseZero = source.IndexOf("case 0:", StringComparison.Ordinal);
            int caseOne = source.IndexOf("case 1:", caseZero, StringComparison.Ordinal);
            int resetIntro = source.IndexOf("HasSeenPreLevelIntro = false;", caseZero, StringComparison.Ordinal);
            int resetIntermission = source.IndexOf("HasSeenIntermission = false;", caseZero, StringComparison.Ordinal);
            int loadSpace = source.IndexOf(
                "SceneManager.LoadSceneAsync(\"Space\", LoadSceneMode.Single);",
                caseZero,
                StringComparison.Ordinal);

            Assert.That(caseZero, Is.GreaterThanOrEqualTo(0));
            Assert.That(caseOne, Is.GreaterThan(caseZero));
            Assert.That(resetIntro, Is.InRange(caseZero + 1, caseOne - 1));
            Assert.That(resetIntermission, Is.InRange(caseZero + 1, caseOne - 1));
            Assert.That(loadSpace, Is.InRange(caseZero + 1, caseOne - 1));
            Assert.That(resetIntro, Is.LessThan(loadSpace));
            Assert.That(resetIntermission, Is.LessThan(loadSpace));
        }
    }
}
