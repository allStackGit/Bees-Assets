using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignCheckpointRoutingTests
    {
        [Test]
        public void CampaignPersistenceMembersRouteThroughAtomicCheckpoint()
        {
            string userData = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "UserData.cs"));
            string checkpoint = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "CampaignCheckpoint.cs"));

            Assert.That(userData, Does.Contain("this is UserProgressData"));
            Assert.That(userData, Does.Contain("ConfigData.SavedSquadsDataFilenames[1]"));
            Assert.That(userData, Does.Contain("ConfigData.FleetDataFilenames[1]"));
            Assert.That(userData, Does.Contain("ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign"));
            Assert.That(userData, Does.Contain("CampaignCheckpoint.Save()"));

            Assert.That(checkpoint, Does.Contain("[ConfigData.UserProgressFilename] = ConfigData.UserProgressData.ToJson()"));
            Assert.That(checkpoint, Does.Contain("[ConfigData.SavedSquadsDataFilenames[1]]"));
            Assert.That(checkpoint, Does.Contain("[ConfigData.FleetDataFilenames[1]]"));
            Assert.That(checkpoint, Does.Contain("ConfigData.Socket.SendRequest"));
        }
    }
}
