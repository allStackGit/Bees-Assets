using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlLongHorizonTrainingTests
    {
        [Test]
        public void CanonicalTrainerUsesLongHorizonCreditSettings()
        {
            string config = ReadSource("Training", "rl_1v1_config.yaml");

            Assert.That(config, Does.Contain("lambd: 0.99"));
            Assert.That(config, Does.Contain("gamma: 1.0"));
            Assert.That(config, Does.Contain("time_horizon: 2048"));
        }

        [Test]
        public void FifteenMinuteEpisodeTimeoutIsAccepted()
        {
            Type optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            object options = RuntimeAssembly.InvokeStatic(
                optionsType,
                "Parse",
                (object)new string[] { "--rl-episode-timeout", "900" });

            string description = (string)RuntimeAssembly.Invoke(options, "Describe");
            Assert.That(description, Does.Contain("episode_timeout=900s"));
        }

        [Test]
        public void TimeoutIsATerminalLossRatherThanAnInterruptedTrajectory()
        {
            Type rewardType = RuntimeAssembly.GetType("RlOneVsOneReward");
            Assert.That(rewardType, Is.Not.Null);

            float timeoutReward = (float)RuntimeAssembly.InvokeStatic(
                rewardType,
                "CalculateTerminalReward",
                1,
                0,
                true);
            Assert.That(timeoutReward, Is.EqualTo(-10f));

            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            int methodStart = agent.IndexOf("private void HandleEpisodeEnded", StringComparison.Ordinal);
            int methodEnd = agent.IndexOf("private bool IsCurrentController", methodStart, StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = agent.Substring(methodStart, methodEnd - methodStart);
            Assert.That(method, Does.Contain("EndEpisode();"));
            Assert.That(method, Does.Not.Contain("EpisodeInterrupted();"));
        }

        private static string ReadSource(params string[] parts)
        {
            string path = Application.dataPath;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path);
        }
    }
}
