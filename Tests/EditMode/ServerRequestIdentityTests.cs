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
        public void RemovingSquadPrunesItsPendingServerRequestsBeforePoolReuse()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs"));

            Assert.That(source, Does.Contain("if (Level != null && Level.IsLevelSetupOnServer)"));
            Assert.That(source, Does.Contain("ConfigData.Socket.StandingRequests.RemoveWhere"));
            Assert.That(source, Does.Contain("commandRequest.SquadId == removedSquadItemId"));
            Assert.That(source, Does.Contain("matchupRequest.SquadId == removedSquadItemId"));
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
