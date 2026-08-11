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
            int shootingAssignment = source.IndexOf("ShootingCommands = temp.ToArray();", StringComparison.Ordinal);
            Assert.That(shootingAssignment, Is.GreaterThanOrEqualTo(0));

            int shootingStart = source.LastIndexOf(
                "shootingCommands.ForEach((storedCommand) =>",
                shootingAssignment,
                StringComparison.Ordinal);
            Assert.That(shootingStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(shootingStart, Is.LessThan(shootingAssignment));

            string shootingBlock = source.Substring(shootingStart, shootingAssignment - shootingStart);
            Assert.That(shootingBlock, Does.Contain("new ServerStoredCommand(storedCommand.ShootingTsv, outcomeId)"));
            Assert.That(shootingBlock, Does.Not.Contain("new ServerStoredCommand(storedCommand.Tsv, outcomeId)"),
                "Shooting outcomes must serialize combat-only ShootingTsv rather than the command's strategic TSV.");
        }

        [Test]
        public void CombatAccountingCreditsDedicatedShootingReward()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Ship.Combat.cs");
            Assert.That(source, Does.Contain("AddShootingTsvToStoredCommand"));
            Assert.That(source, Does.Contain("CreditShootingTsv(attacker, tsvDelta, attackerCommandOutcomeId)"));
            Assert.That(source, Does.Contain("CreditShootingTsv(target, -tsvLoss)"));
        }

        [Test]
        public void SameSideDamageAlwaysPenalizesTheAttackingCommand()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Ship.Combat.cs");
            Assert.That(source, Does.Contain("_isFriendlyFire = attackerFleetShip.Side == target.Side;"));
            Assert.That(source, Does.Contain("tsvLoss * (_isFriendlyFire ? -1 : 1)"));
            Assert.That(source, Does.Not.Contain("else if (attacker.KillerFleetShip != null)\n            {\n                _isFriendlyFire = true;"),
                "Friendly-fire classification must not depend on whether the explosive attacker has external killer metadata.");
        }

        [Test]
        public void FireBargeChainReactionKeepsOriginalKillerOutcome()
        {
            string fireBarge = ReadSource("Scripts", "Entities", "Ships", "FireBarge.cs");
            string combat = ReadSource("Scripts", "Entities", "Ships", "Ship.Combat.cs");

            Assert.That(fireBarge, Does.Contain("KillerCommandOutcomeId = killerCommand != null && killerCommand.IsHiveMindCommand"));
            Assert.That(fireBarge, Does.Contain("KillerCommandOutcomeId = 0;"),
                "The pooled Fire Barge wrapper must not retain a previous killer outcome.");
            Assert.That(combat, Does.Contain("CreditAttackerCommandTsv(attacker.Killer, tsvLoss, attacker.KillerCommandOutcomeId)"));
            Assert.That(combat, Does.Not.Contain("attacker.Killer.Squad.GetCommand().Tsv += tsvLoss"),
                "Delayed chain-reaction reward must not be assigned to whichever killer command is active at impact time.");
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
