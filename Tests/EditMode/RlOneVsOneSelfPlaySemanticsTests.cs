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
        public void TimeoutRemainsAnExplicitTerminalLossRatherThanAnInterruptedTrajectory()
        {
            string reward = ReadSource("Scripts", "Scenes", "RlOneVsOneReward.cs");
            Assert.That(reward, Does.Contain("LossReward = -10f"));
            Assert.That(reward, Does.Contain("if (winningSide == 0)"));
            Assert.That(reward, Does.Contain("return LossReward;"));

            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            int timeoutComment = agent.IndexOf("Timeouts are explicit terminal losses in this environment", StringComparison.Ordinal);
            int endEpisode = agent.IndexOf("EndEpisode();", timeoutComment, StringComparison.Ordinal);

            Assert.That(timeoutComment, Is.GreaterThanOrEqualTo(0));
            Assert.That(endEpisode, Is.GreaterThan(timeoutComment));
            Assert.That(agent, Does.Not.Contain("EpisodeInterrupted();"),
                "A timeout is a game-terminal loss and must not bootstrap through an interrupted trajectory.");
        }

        [Test]
        public void ConstantPolicyDirectionMapsToFourDifferentWorldDirections()
        {
            Type frameType = RuntimeAssembly.GetType("RlPolicyCoordinateFrame");
            Vector2[] expected =
            {
                Vector2.up,
                Vector2.right,
                Vector2.down,
                Vector2.left
            };

            for (int quarterTurns = 0; quarterTurns < expected.Length; quarterTurns++)
            {
                Vector2 actual = (Vector2)RuntimeAssembly.InvokeStatic(
                    frameType,
                    "PolicyToWorld",
                    Vector2.up,
                    quarterTurns);
                Assert.That(actual.x, Is.EqualTo(expected[quarterTurns].x).Within(0.0001f));
                Assert.That(actual.y, Is.EqualTo(expected[quarterTurns].y).Within(0.0001f));
            }
        }

        [Test]
        public void EnemyDirectedMovementAndAimRemainEquivariantAcrossFrames()
        {
            Type frameType = RuntimeAssembly.GetType("RlPolicyCoordinateFrame");
            Vector2 worldDirection = new Vector2(4f, -2f).normalized;

            for (int quarterTurns = 0; quarterTurns < 4; quarterTurns++)
            {
                Vector2 policyDirection = (Vector2)RuntimeAssembly.InvokeStatic(
                    frameType,
                    "WorldToPolicy",
                    worldDirection,
                    quarterTurns);
                Vector2 reconstructedWorldDirection = (Vector2)RuntimeAssembly.InvokeStatic(
                    frameType,
                    "PolicyToWorld",
                    policyDirection,
                    quarterTurns);

                Assert.That(reconstructedWorldDirection.x, Is.EqualTo(worldDirection.x).Within(0.0001f));
                Assert.That(reconstructedWorldDirection.y, Is.EqualTo(worldDirection.y).Within(0.0001f));
            }
        }

        [Test]
        public void TeamFramesAreDistinctStableWithinEpisodeAndRefreshAfterEpisodeEnd()
        {
            Type frameType = RuntimeAssembly.GetType("RlPolicyCoordinateFrame");
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            GameObject levelObject = new GameObject("RL Coordinate Frame Test Level");
            try
            {
                Component level = levelObject.AddComponent(levelType);
                RuntimeAssembly.InvokeStatic(frameType, "ResetForTests");

                int team0First = (int)RuntimeAssembly.InvokeStatic(frameType, "GetQuarterTurns", level, 0);
                int team1First = (int)RuntimeAssembly.InvokeStatic(frameType, "GetQuarterTurns", level, 1);
                int generationAfterFirstAssignment = (int)RuntimeAssembly.InvokeStatic(
                    frameType,
                    "GetAssignmentGenerationForTests");

                int team0Second = (int)RuntimeAssembly.InvokeStatic(frameType, "GetQuarterTurns", level, 0);
                int team1Second = (int)RuntimeAssembly.InvokeStatic(frameType, "GetQuarterTurns", level, 1);
                int generationAfterRepeatedReads = (int)RuntimeAssembly.InvokeStatic(
                    frameType,
                    "GetAssignmentGenerationForTests");

                Assert.That(team0First, Is.Not.EqualTo(team1First),
                    "Opposing self-play teams must never receive the same absolute coordinate frame.");
                Assert.That(team0Second, Is.EqualTo(team0First));
                Assert.That(team1Second, Is.EqualTo(team1First));
                Assert.That(generationAfterFirstAssignment, Is.EqualTo(1));
                Assert.That(generationAfterRepeatedReads, Is.EqualTo(1),
                    "Reading the frame repeatedly during an episode must not resample it.");

                RuntimeAssembly.InvokeStatic(frameType, "EndEpisode");
                RuntimeAssembly.InvokeStatic(frameType, "GetQuarterTurns", level, 0);
                int generationAfterEpisodeEnd = (int)RuntimeAssembly.InvokeStatic(
                    frameType,
                    "GetAssignmentGenerationForTests");
                Assert.That(generationAfterEpisodeEnd, Is.EqualTo(2),
                    "A new environment episode must receive a fresh frame assignment even if it reuses the same Level object.");
            }
            finally
            {
                RuntimeAssembly.InvokeStatic(frameType, "ResetForTests");
                UnityEngine.Object.DestroyImmediate(levelObject);
            }
        }

        [Test]
        public void NavigationGridUsesTheSamePolicyToWorldRotationAsActions()
        {
            Type frameType = RuntimeAssembly.GetType("RlPolicyCoordinateFrame");
            const int gridSize = 13;
            int center = gridSize / 2;
            int policyCenter = center * gridSize + center;
            int policyUp = (center + 1) * gridSize + center;

            for (int quarterTurns = 0; quarterTurns < 4; quarterTurns++)
            {
                int mappedCenter = (int)RuntimeAssembly.InvokeStatic(
                    frameType,
                    "WorldGridIndexForPolicyIndex",
                    policyCenter,
                    gridSize,
                    quarterTurns);
                Assert.That(mappedCenter, Is.EqualTo(policyCenter));
            }

            int worldIndexForPolicyUpAtQuarterTurnOne = (int)RuntimeAssembly.InvokeStatic(
                frameType,
                "WorldGridIndexForPolicyIndex",
                policyUp,
                gridSize,
                1);
            int worldRight = center * gridSize + center + 1;
            Assert.That(worldIndexForPolicyUpAtQuarterTurnOne, Is.EqualTo(worldRight));
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
