using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MainMenuCampaignReadinessTests
    {
        [Test]
        public void CampaignButtonWaitsForMainMenuFinalization()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "MainMenuCampaignReadinessGuard.cs"));

            Assert.That(source, Does.Contain("_campaignButton.interactable = false;"),
                "Campaign entry must be disabled as soon as Main Menu loads.");
            Assert.That(source, Does.Contain("!_mainMenu.IsFinalized"),
                "Campaign entry must remain blocked until server-backed Main Menu finalization completes.");
            Assert.That(source, Does.Contain("_campaignButton.interactable = !campaignComplete;"),
                "Campaign entry should only be released after finalization and must remain disabled when the campaign is complete.");
        }
    }
}
