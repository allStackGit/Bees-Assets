using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TargetingRewardIsolationTests
    {
        [Test]
        public void TargetingPersistenceRequiresEnemyDependentCommand()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs"));

            Assert.That(source, Does.Contain("CommandUsesSelectedEnemy(command.CommandType)"));
            Assert.That(source, Does.Contain("case ConfigData.CommandTypes.Aggressive:"));
            Assert.That(source, Does.Contain("case ConfigData.CommandTypes.BombingRun:"));
            Assert.That(source, Does.Contain("case ConfigData.CommandTypes.Charge:"));
            Assert.That(source, Does.Contain("case ConfigData.CommandTypes.Retreat:"));
            Assert.That(source, Does.Contain("case ConfigData.CommandTypes.CircleSquad:"));
            Assert.That(source, Does.Contain("case ConfigData.CommandTypes.RightSwipe:"));
            Assert.That(source, Does.Contain("case ConfigData.CommandTypes.LeftSwipe:"));
            Assert.That(source, Does.Contain("case ConfigData.CommandTypes.InAndOut:"));
        }

        [TestCase("Mining")]
        [TestCase("Heal")]
        [TestCase("Patrol")]
        [TestCase("Guard")]
        [TestCase("Scouting")]
        [TestCase("FullRetreat")]
        [TestCase("Hold")]
        [TestCase("MoveToRandom")]
        [TestCase("ClosestFriendly")]
        public void NonEnemyCommandsAreNotTargetingTrainingTypes(string commandName)
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs"));
            string methodStartToken = "private static bool CommandUsesSelectedEnemy";
            int start = source.IndexOf(methodStartToken);
            int end = source.IndexOf("public void AddToSquadsAwaitingHiveMindCommands", start);
            string method = source.Substring(start, end - start);

            Assert.That(method, Does.Not.Contain($"ConfigData.CommandTypes.{commandName}"));
        }
    }
}
