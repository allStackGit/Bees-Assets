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
        public void EveryProfileMemberRoutesThroughTheAtomicServerCheckpoint()
        {
            string dataFile = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "DataFile.cs"));
            string checkpoint = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "CampaignCheckpoint.cs"));
            string userData = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "UserData.cs"));
            string lifecycleGuard = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SocketResponseLifecycleGuard.cs"));

            Assert.That(dataFile, Does.Contain("CampaignCheckpoint.IsProfileMember(Name)"));
            Assert.That(dataFile, Does.Contain("CampaignCheckpoint.Save()"));
            Assert.That(dataFile, Does.Contain("!ConfigData.Test"),
                "Local test storage must not recurse through the live-server checkpoint hook.");

            Assert.That(checkpoint, Does.Contain("filename == ConfigData.UserProgressFilename"));
            Assert.That(checkpoint, Does.Contain("filename == ConfigData.FleetDataFilenames[i]"));
            Assert.That(checkpoint, Does.Contain("filename == ConfigData.SavedSquadsDataFilenames[i]"));
            for (int index = 0; index < 3; index++)
            {
                Assert.That(checkpoint, Does.Contain($"[ConfigData.FleetDataFilenames[{index}]]"));
                Assert.That(checkpoint, Does.Contain($"[ConfigData.SavedSquadsDataFilenames[{index}]]"));
            }
            Assert.That(checkpoint, Does.Contain("[ConfigData.UserProgressFilename] = ConfigData.UserProgressData.ToJson()"));
            Assert.That(checkpoint, Does.Contain("ConfigData.IsFleetDataLoaded[i]"));
            Assert.That(checkpoint, Does.Contain("ConfigData.IsSavedSquadsDataLoaded[i]"));
            Assert.That(checkpoint, Does.Contain("ConfigData.SocketManager == null"));
            Assert.That(checkpoint, Does.Contain("_pendingSave = true"));
            Assert.That(checkpoint, Does.Contain("ConfigData.Socket.SendRequest"));
            Assert.That(lifecycleGuard, Does.Contain("CampaignCheckpoint.FlushIfReady()"),
                "The existing persistent socket guard should own deferred checkpoint flushing.");

            Assert.That(userData, Does.Not.Contain("ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign"),
                "Profile transaction routing belongs at the DataFile boundary so reset/default writes cannot bypass it.");
        }
    }
}
