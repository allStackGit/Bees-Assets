using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignResourceAccountingTests
    {
        [Test]
        public void PlayerMiningCollectionIgnoresNonUserSquads()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Endings.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf("private void CollectMinedMineralsForPlayer()");
            int methodEnd = source.IndexOf("private void SaveCampaignProgress()", methodStart);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, methodEnd - methodStart);
            StringAssert.Contains("_save_savedSquad.Side != ConfigData.Configuration.UserSide", method);
            StringAssert.Contains("continue;", method);
            StringAssert.Contains("State.PlayerMineralsReceived += _save_fleetship.MineralsMinedThisLevel", method);
        }

        [Test]
        public void DesignedCampaignFailureBranchesStillSkipMiningMissions()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Endings.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("else\n            {\n                ConfigData.UserProgressData.AdvanceToNextLevel();\n                int mineralsMined = 0;", source);
            StringAssert.Contains("WinningSide == ConfigData.Configuration.AISide || !ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Factory)", source);
        }
    }
}
