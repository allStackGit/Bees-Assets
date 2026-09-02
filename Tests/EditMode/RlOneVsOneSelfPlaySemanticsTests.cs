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
            int assignedTeam = agent.IndexOf("int assignedTeam = _side == ConfigData.Configuration.BeeSide", StringComparison.Ordinal);
            int idleGuard = agent.IndexOf("if (_teamId != assignedTeam || !_hasParticipatedThisEpisode)", assignedTeam, StringComparison.Ordinal);
            int idleReturn = agent.IndexOf("return;", idleGuard, StringComparison.Ordinal);
            int activeReward = agent.IndexOf("AddReward(_side == ConfigData.Configuration.BeeSide", idleReturn, StringComparison.Ordinal);
            int endEpisode = agent.IndexOf("EndEpisode();", activeReward, StringComparison.Ordinal);

            Assert.That(assignedTeam, Is.GreaterThanOrEqualTo(0));
            Assert.That(idleGuard, Is.GreaterThan(assignedTeam));
            Assert.That(idleReturn, Is.GreaterThan(idleGuard),
                "An idle self-play role must return without creating an empty ML-Agents trajectory.");
            Assert.That(activeReward, Is.GreaterThan(idleReturn));
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
