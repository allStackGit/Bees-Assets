using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlContinualEvaluationSideChannelTests
    {
        private const string EvaluationChannelId = "7ca0e8e5-47f7-49ce-b44a-738ae7f1ad15";
        private const string EvaluationModeFlag = "--bees-rl-evaluator";

        [Test]
        public void ResultChannelRegistersOnlyForExplicitEvaluatorRuns()
        {
            Type channelType = RuntimeAssembly.GetType("RlOneVsOneEvaluationSideChannel");
            MethodInfo shouldRegister = channelType.GetMethod(
                "ShouldRegister",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(shouldRegister, Is.Not.Null);

            object normalTraining = shouldRegister.Invoke(
                null,
                new object[] { "RL 1v1 Training", Array.Empty<string>(), false });
            object evaluator = shouldRegister.Invoke(
                null,
                new object[] { "RL 1v1 Training", new[] { EvaluationModeFlag }, false });
            object wrongScene = shouldRegister.Invoke(
                null,
                new object[] { "Title", new[] { EvaluationModeFlag }, false });
            object alreadyRegistered = shouldRegister.Invoke(
                null,
                new object[] { "RL 1v1 Training", new[] { EvaluationModeFlag }, true });

            Assert.That(normalTraining, Is.False,
                "Ordinary training must not register a side channel that its Python trainer does not know about.");
            Assert.That(evaluator, Is.True);
            Assert.That(wrongScene, Is.False);
            Assert.That(alreadyRegistered, Is.False);
        }

        [Test]
        public void WinningSideMapsToFrozenPolicyTeamAndTimeoutsRemainDraws()
        {
            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object previousConfiguration = RuntimeAssembly.GetStaticField(configDataType, "Configuration");
            object configuration = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(configuration, "BeeSide", 4);
            RuntimeAssembly.SetField(configuration, "HumanSide", 9);
            RuntimeAssembly.SetStaticField(configDataType, "Configuration", configuration);

            try
            {
                Type channelType = RuntimeAssembly.GetType("RlOneVsOneEvaluationSideChannel");
                Type episodeResultType = RuntimeAssembly.GetType("RlOneVsOneEpisodeCoordinator+EpisodeResult");
                MethodInfo getWinningTeamId = channelType.GetMethod(
                    "GetWinningTeamId",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    null,
                    new[] { episodeResultType },
                    null);
                Assert.That(getWinningTeamId, Is.Not.Null);

                Assert.That(
                    getWinningTeamId.Invoke(null, new[] { CreateEpisodeResult(4, false) }),
                    Is.EqualTo(17));
                Assert.That(
                    getWinningTeamId.Invoke(null, new[] { CreateEpisodeResult(9, false) }),
                    Is.EqualTo(23));
                Assert.That(
                    getWinningTeamId.Invoke(null, new[] { CreateEpisodeResult(0, false) }),
                    Is.EqualTo(-1));
                Assert.That(
                    getWinningTeamId.Invoke(null, new[] { CreateEpisodeResult(4, true) }),
                    Is.EqualTo(-1));
                Assert.That(
                    getWinningTeamId.Invoke(null, new[] { CreateEpisodeResult(99, false) }),
                    Is.EqualTo(-1));
            }
            finally
            {
                RuntimeAssembly.SetStaticField(configDataType, "Configuration", previousConfiguration);
            }
        }

        private static object CreateEpisodeResult(int winningSide, bool timedOut)
        {
            Type resultType = RuntimeAssembly.GetType("RlOneVsOneEpisodeCoordinator+EpisodeResult");
            ConstructorInfo[] constructors = resultType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors, Has.Length.EqualTo(1));

            return constructors[0].Invoke(new object[]
            {
                7,
                17,
                23,
                winningSide,
                timedOut,
                12.5f,
                16,
                4,
                5,
                2,
                4,
                2,
                100,
                3,
                1,
                60,
                10f,
                0.2f,
                -0.1f,
                -10f,
                -0.2f,
                0f,
            });
        }

        private static void AssertTokensInOrder(string source, params string[] tokens)
        {
            int searchFrom = 0;
            foreach (string token in tokens)
            {
                int index = source.IndexOf(token, searchFrom, StringComparison.Ordinal);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), $"Missing or out-of-order protocol token: {token}");
                searchFrom = index + token.Length;
            }
        }

    }
}