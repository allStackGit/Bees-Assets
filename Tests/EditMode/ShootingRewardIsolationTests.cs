using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ShootingRewardIsolationTests
    {
        private static string ReadSource(params string[] parts)
        {
            string path = Application.dataPath;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path);
        }

        [Test]
        public void ShootingOutcomesSerializeCombatOnlyReward()
        {
            string source = ReadSource("Scripts", "Server", "StoreCommands.cs");
            Assert.That(source, Does.Contain("new ServerStoredCommand(storedCommand.ShootingTsv, storedCommand.ShootingStrategy.OutcomeId)"));
            Assert.That(source, Does.Not.Contain("new ServerStoredCommand(storedCommand.Tsv, storedCommand.ShootingStrategy.OutcomeId)"));
        }

        [Test]
        public void CombatAccountingCreditsDedicatedShootingReward()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Ship.Combat.cs");
            Assert.That(source, Does.Contain("AddShootingTsvToStoredCommand"));
            Assert.That(source, Does.Contain("CreditShootingTsv(attacker, tsvDelta, attackerCommandOutcomeId)"));
            Assert.That(source, Does.Contain("CreditShootingTsv(target, -tsvLoss)"));
        }

        [TestCase("Mining.cs")]
        [TestCase("Heal.cs")]
        [TestCase("FullRetreat.cs")]
        public void CommandSpecificRewardsDoNotWriteShootingReward(string filename)
        {
            string source = ReadSource("Scripts", "Levels", "Commands", filename);
            Assert.That(source, Does.Not.Contain("ShootingTsv"),
                $"{filename} must leave shooting reward to combat accounting rather than copying command-specific TSV.");
            Assert.That(source, Does.Not.Contain("AddShootingTsvToStoredCommand"));
        }

        [Test]
        public void VisionRewardDoesNotWriteShootingReward()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Weapons", "HivemindVision.cs");
            Assert.That(source, Does.Not.Contain("ShootingTsv"));
            Assert.That(source, Does.Not.Contain("AddShootingTsvToStoredCommand"));
        }

        [Test]
        public void StoredCommandStartsShootingRewardAtZero()
        {
            string source = ReadSource("Scripts", "Levels", "StoredCommand.cs");
            Assert.That(source, Does.Contain("ShootingTsv = 0;"));
        }

        [Test]
        public void RetreatIsNotPersistedAsServerSelectedShootingBehaviorWhileSocketForcesFirstSeen()
        {
            string socket = ReadSource("Scripts", "Server", "Socket.cs");
            string state = ReadSource("Scripts", "Levels", "GameState.Commands.cs");

            Assert.That(socket, Does.Contain("((Retreat)_tempSquad.GetCommand()).Execute(ConfigData.ShootingStrategyTypes.FirstSeen"),
                "If Retreat begins honoring the server-selected shooting strategy, this containment test should be updated and the exclusion can be removed.");
            Assert.That(state, Does.Contain("command.CommandType != ConfigData.CommandTypes.Retreat"),
                "Retreat must not train the server-selected shooting outcome while executing FirstSeen instead.");
        }
    }
}
