using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ServerRequestIdentityTests
    {
        [Test]
        public void CommandRequestRejectsDeadOrRecycledEnemyWrapper()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "CommandRequest.cs"));

            Assert.That(source, Does.Contain("public readonly int EnemyId;"));
            Assert.That(source, Does.Contain("EnemyId = enemy != null ? enemy.ItemId : 0;"));
            Assert.That(source, Does.Contain("public Squad Enemy => _enemy != null && !_enemy.IsDead && _enemy.ItemId == EnemyId ? _enemy : null;"));
        }

        [Test]
        public void DeadSquadRequestsAreRejectedByRuntimeIdentityWithoutScanningSocketDuringDeath()
        {
            string registry = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));
            string commandRequest = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "CommandRequest.cs"));
            string matchupRequest = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "MatchupStrategyRequest.cs"));
            string socket = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "Socket.cs"));

            Assert.That(registry, Does.Not.Contain("StandingRequests.RemoveWhere"),
                "Squad death must remain local/bounded rather than scanning global socket state.");
            Assert.That(commandRequest, Does.Contain("SquadId == Squad.ItemId && !Squad.IsDead"));
            Assert.That(matchupRequest, Does.Contain("Squad.ItemId == SquadId && !Squad.IsDead"));
            Assert.That(socket, Does.Contain("squad.ItemId == expectedItemId"));
            Assert.That(socket, Does.Contain("!squad.IsDead"));
            Assert.That(socket, Does.Contain("TakeStandingRequest("),
                "Stale strategic responses should still consume their standing request before identity validation.");
        }

        [Test]
        public void StrategicRequestsAlwaysBanMoveToPointWithoutDestinationPayload()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "GetStrategy.cs"));

            Assert.That(source, Does.Contain(".Concat(new[] { \"Move To Point\" })"));
            Assert.That(source, Does.Contain(".Distinct()"));
        }

        [Test]
        public void SettingsRequestRecoversFromHandledButIncompleteResponseWithFreshRequest()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Settings", "ServerSettings.cs"));

            Assert.That(source, Does.Contain("if (standingRequest == null)"));
            Assert.That(source, Does.Contain("ConfigData.Socket.HandledRequests.Contains(_request.Hash)"));
            Assert.That(source, Does.Contain("ConfigData.Socket.StandingRequests.Remove(standingRequest);"));
            Assert.That(source, Does.Contain("Fetch();"));
        }
    }
}
