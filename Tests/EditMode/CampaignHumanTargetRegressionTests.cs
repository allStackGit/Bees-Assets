using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignHumanTargetRegressionTests
    {
        [Test]
        public void CreateHumanTargetDoesNotAssumeASquadTabExists()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Level.Campaign.Shared.cs"));

            StringAssert.Contains("if (humanTarget.Squad.SquadTab != null)", source);
            StringAssert.Contains("Destroy(humanTarget.Squad.SquadTab.gameObject);", source);
            StringAssert.Contains("humanTarget.Squad.SquadTab = null;", source);
            StringAssert.Contains("humanTarget.Squad.HasSquadTab = false;", source);
        }
    }
}
