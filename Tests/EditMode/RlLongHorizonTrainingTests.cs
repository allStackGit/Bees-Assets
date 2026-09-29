using System;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlLongHorizonTrainingTests
    {
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
            Assert.That(timeoutReward, Is.EqualTo(-1.1f));

            float ordinaryLossReward = (float)RuntimeAssembly.InvokeStatic(
                rewardType,
                "CalculateTerminalReward",
                1,
                2,
                false);
            Assert.That(ordinaryLossReward, Is.EqualTo(-1f));


        }

    }
}