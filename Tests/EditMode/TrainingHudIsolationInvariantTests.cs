using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TrainingHudIsolationInvariantTests
    {
        [Test]
        public void CampaignHudIndexingIsSkippedAfterTrainingRemovesUiElements()
        {
            string reset = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Reset.cs"));
            string bootstrap = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Scenes", "HiveMindTrainingBootstrap.cs"));

            Assert.That(bootstrap, Does.Contain("stage.UIElements.RemoveAt(i)"),
                "Dedicated training removes UI objects from the Stage list before level setup.");
            Assert.That(reset, Does.Contain("ConfigData.GameModes.Campaign && !Stage.IsTraining"),
                "Campaign HUD indexing must be skipped when training has removed or shifted those elements.");
        }
    }
}
