using System;
using System.IO;
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
        public void UnityResultChannelMatchesPythonEvaluatorProtocol()
        {
            string unity = ReadSource("Scripts", "Scenes", "RlOneVsOneEvaluationSideChannel.cs");
            string python = ReadSource("Training", "bees_continual_evaluate.py");

            Assert.That(unity, Does.Contain($"ChannelIdText = \"{EvaluationChannelId}\";"));
            Assert.That(python, Does.Contain($"EVALUATION_CHANNEL_ID = uuid.UUID(\"{EvaluationChannelId}\")"));
            Assert.That(unity, Does.Contain("internal const int ProtocolVersion = 1;"));
            Assert.That(python, Does.Contain("EVALUATION_PROTOCOL_VERSION = 1"));
            Assert.That(unity, Does.Contain($"EvaluationModeFlag = \"{EvaluationModeFlag}\";"));
            Assert.That(python, Does.Contain($"EVALUATION_MODE_FLAG = \"{EvaluationModeFlag}\""));

            AssertTokensInOrder(
                unity,
                "message.WriteInt32(ProtocolVersion);",
                "message.WriteInt32(result.EpisodeNumber);",
                "message.WriteInt32(result.BeeTeamId);",
                "message.WriteInt32(result.HumanTeamId);",
                "message.WriteInt32(result.WinningSide);",
                "message.WriteInt32(GetWinningTeamId(result));",
                "message.WriteBoolean(result.TimedOut);",
                "message.WriteFloat32(result.DurationSeconds);",
                "message.WriteInt32(result.BeeStartingTsv);",
                "message.WriteInt32(result.BeeFinalTsv);",
                "message.WriteInt32(result.HumanStartingTsv);",
                "message.WriteInt32(result.HumanFinalTsv);",
                "message.WriteInt32(result.BeeShotsFired);",
                "message.WriteInt32(result.BeeShotsHit);",
                "message.WriteInt32(result.BeeDamageDealt);",
                "message.WriteInt32(result.HumanShotsFired);",
                "message.WriteInt32(result.HumanShotsHit);",
                "message.WriteInt32(result.HumanDamageDealt);");

            AssertTokensInOrder(
                python,
                "version = message.read_int32()",
                "episode_number=message.read_int32(),",
                "bee_team_id=message.read_int32(),",
                "human_team_id=message.read_int32(),",
                "winning_side=message.read_int32(),",
                "winning_team_id=message.read_int32(),",
                "timed_out=message.read_bool(),",
                "duration_seconds=float(message.read_float32()),",
                "bee_starting_tsv=message.read_int32(),",
                "bee_final_tsv=message.read_int32(),",
                "human_starting_tsv=message.read_int32(),",
                "human_final_tsv=message.read_int32(),",
                "bee_shots=message.read_int32(),",
                "bee_hits=message.read_int32(),",
                "bee_damage=message.read_int32(),",
                "human_shots=message.read_int32(),",
                "human_hits=message.read_int32(),",
                "human_damage=message.read_int32(),");
        }

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
        public void ResultChannelUnregistersBeforeItsStaticInstanceIsCleared()
        {
            string channel = ReadSource("Scripts", "Scenes", "RlOneVsOneEvaluationSideChannel.cs");
            AssertTokensInOrder(
                channel,
                "RlOneVsOneEpisodeCoordinator.EpisodeEnded -= OnEpisodeEnded;",
                "SideChannelManager.UnregisterSideChannel(_instance);",
                "_instance = null;");
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
                MethodInfo getWinningTeamId = channelType.GetMethod(
                    "GetWinningTeamId",
                    BindingFlags.Static | BindingFlags.NonPublic);
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

        [Test]
        public void EpisodeCompletionPublishesOneFinishedResultToTheEvaluatorHook()
        {
            Type coordinatorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeCoordinator");
            EventInfo episodeEnded = coordinatorType.GetEvent(
                "EpisodeEnded",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(episodeEnded, Is.Not.Null);

            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            int buildResult = coordinator.IndexOf("EpisodeResult result = new EpisodeResult(", StringComparison.Ordinal);
            int retainResult = coordinator.IndexOf("LastEpisodeResult = result;", buildResult, StringComparison.Ordinal);
            int closeEpisode = coordinator.IndexOf("_episodeActive = false;", retainResult, StringComparison.Ordinal);
            int publishResult = coordinator.IndexOf("EpisodeEnded?.Invoke(level, result);", closeEpisode, StringComparison.Ordinal);

            Assert.That(buildResult, Is.GreaterThanOrEqualTo(0));
            Assert.That(retainResult, Is.GreaterThan(buildResult));
            Assert.That(closeEpisode, Is.GreaterThan(retainResult));
            Assert.That(publishResult, Is.GreaterThan(closeEpisode));
            Assert.That(
                coordinator,
                Does.Contain("coordinator.CompleteEpisode(level, DetermineWinner(level), false);"));
            Assert.That(
                coordinator,
                Does.Contain("coordinator.CompleteEpisode(level, 0, true);"));

            string channel = ReadSource("Scripts", "Scenes", "RlOneVsOneEvaluationSideChannel.cs");
            Assert.That(channel, Does.Contain("RlOneVsOneEpisodeCoordinator.EpisodeEnded += OnEpisodeEnded;"));
            Assert.That(channel, Does.Contain("SideChannelManager.RegisterSideChannel(_instance);"));
            Assert.That(channel, Does.Contain("SideChannelManager.UnregisterSideChannel(_instance);"));
            Assert.That(channel, Does.Contain("IsEvaluationMode(args)"));
            Assert.That(channel, Does.Not.Contain("msg.Read"),
                "The authoritative result channel must remain output-only; evaluation configuration belongs to command-line options.");
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

        private static string ReadSource(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}