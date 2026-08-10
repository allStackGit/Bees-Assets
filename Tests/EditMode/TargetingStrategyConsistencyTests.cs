using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TargetingStrategyConsistencyTests
    {
        private static string Read(params string[] path) => File.ReadAllText(Path.Combine(Application.dataPath, Path.Combine(path)));

        [Test]
        public void CommandLeastHealthOrdersByRemainingHealth()
        {
            string command = Read("Scripts", "Levels", "Commands", "Command.cs");

            Assert.That(command, Does.Contain("case ConfigData.ShootingStrategyTypes.LeastHealth:"));
            Assert.That(command, Does.Contain("_tempShips.Sort((a, b) => a.Health.CompareTo(b.Health));"));
            Assert.That(command, Does.Not.Contain("(a.Health - a.OriginalHealth).CompareTo(b.Health - b.OriginalHealth)"));
        }

        [Test]
        public void MatchupTypeXUsesNormalTypeTargetingBranch()
        {
            string matchup = Read("Scripts", "Levels", "Commands", "MatchupStrategy.cs");

            Assert.That(matchup, Does.Contain("case ConfigData.MatchupStrategyTypes.TypeX:"));
            Assert.That(matchup, Does.Contain("_type = Utilities.ConvertMatchupStrategyToShipType[MatchupType];"));
        }
    }
}
