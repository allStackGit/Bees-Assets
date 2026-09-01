using System;
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
            int idleGuard = agent.IndexOf("if (_teamId != assignedTeamId)", StringComparison.Ordinal);
            int activeReward = agent.IndexOf("// TSV shaping was already delivered", StringComparison.Ordinal);
            int endEpisode = agent.IndexOf("EndEpisode();", idleGuard, StringComparison.Ordinal);

            Assert.That(idleGuard, Is.GreaterThanOrEqualTo(0));
            Assert.That(agent, Does.Contain("zero-step trajectory"));
            Assert.That(activeReward, Is.GreaterThan(idleGuard));
            Assert.That(endEpisode, Is.GreaterThan(activeReward));
        }

        [Test]
        public void TimeoutKeepsFailureRewardButIsExcludedFromCompetitiveElo()
        {
            string reward = ReadSource("Scripts", "Scenes", "RlOneVsOneReward.cs");
            Assert.That(reward, Does.Contain("LossReward = -10f"));
            Assert.That(reward, Does.Contain("if (winningSide == 0)"));
            Assert.That(reward, Does.Contain("return LossReward;"));

            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            int timeoutBranch = agent.IndexOf("if (result.TimedOut)", StringComparison.Ordinal);
            int interrupted = agent.IndexOf("EpisodeInterrupted();", StringComparison.Ordinal);
            int decisiveEnd = agent.IndexOf("EndEpisode();", interrupted, StringComparison.Ordinal);

            Assert.That(timeoutBranch, Is.GreaterThanOrEqualTo(0));
            Assert.That(interrupted, Is.GreaterThan(timeoutBranch));
            Assert.That(decisiveEnd, Is.GreaterThan(interrupted));
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
