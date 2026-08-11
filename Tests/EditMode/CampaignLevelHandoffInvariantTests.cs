using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignLevelHandoffInvariantTests
    {
        [Test]
        public void CampaignLevelOptionsAssignmentIsNormalizedAgainstCurrentProgress()
        {
            string core = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ConfigData.cs"));
            string campaign = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ConfigData.Campaign.cs"));

            Assert.That(core, Does.Contain("set => _levelOptions = NormalizeCampaignLevelOptions(value);"),
                "Campaign level handoffs must pass through the mission-identity guard before Stage can read them.");
            Assert.That(campaign, Does.Contain("candidate.Id == currentMissionId"));
            Assert.That(campaign, Does.Contain("GetCampaignLevelData().GetLevel(currentMissionId)"));
            Assert.That(campaign, Does.Contain("corrected.ChosenSquads"),
                "Correcting mission identity must preserve the squads selected by the player.");
        }

        [Test]
        public void ExplicitNegativeIdTestLevelsRemainAllowed()
        {
            string campaign = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ConfigData.Campaign.cs"));
            Assert.That(campaign, Does.Contain("candidate.Id < 0"),
                "Squad Maker test/custom levels use negative IDs and must not be rewritten as campaign missions.");
        }
    }
}
