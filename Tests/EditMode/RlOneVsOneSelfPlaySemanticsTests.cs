using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneSelfPlaySemanticsTests
    {
        [Test]
        public void IdleRoleAgentsDoNotEmitSyntheticZeroStepTrajectories()
        {
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            Assert.That(agent, Does.Contain("if (_teamId != assignedTeamId)"));
            Assert.That(agent, Does.Contain("zero-step trajectory"));
            Assert.That(agent, Does.Contain("return;\n        }\n\n        // TSV shaping was already delivered"));
        }

        [Test]
        public void TimeoutKeepsFailureRewardButIsExcludedFromCompetitiveElo()
        {
            string reward = ReadSource("Scripts", "Scenes", "RlOneVsOneReward.cs");
            Assert.That(reward, Does.Contain("LossReward = -10f"));
            Assert.That(reward, Does.Contain("if (winningSide == 0)"));
            Assert.That(reward, Does.Contain("return LossReward;"));

            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            Assert.That(agent, Does.Contain("if (result.TimedOut)"));
            Assert.That(agent, Does.Contain("EpisodeInterrupted();"));
            Assert.That(agent, Does.Contain("else\n        {\n            EndEpisode();"));
        }

        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < pathParts.Length; i++)
            {
                path = Path.Combine(path, pathParts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}
