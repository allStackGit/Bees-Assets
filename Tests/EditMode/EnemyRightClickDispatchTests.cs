using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class EnemyRightClickDispatchTests
    {
        [Test]
        public void EnemyRightClickDoesNotBuildWholeMapConnectivityOnInputFrame()
        {
            string interaction = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Interaction.cs"));
            string aggressive = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Commands", "Aggressive.cs"));
            string playerCommands = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs"));

            Assert.That(interaction, Does.Contain("private static int _lastEnemyRightClickFrame = -1;"));
            Assert.That(interaction, Does.Contain("_lastEnemyRightClickFrame == Time.frameCount"));
            Assert.That(interaction, Does.Not.Contain("AreStaticallyConnected("),
                "Right-click input must not lazily flood-fill the pathfinder grid on Unity's main thread.");
            Assert.That(interaction, Does.Contain("TryPlayerTargetEnemy("),
                "Enemy clicks must pass through player ownership authorization.");
            Assert.That(playerCommands, Does.Contain("squad.UserTargetEnemy(enemySquad);"),
                "Authorized enemy clicks must still use composition-aware dispatch so Barge squads can charge.");

            Assert.That(aggressive, Does.Contain("ship.MoveToTrackedPoint(target.GetPosition());"));
            Assert.That(aggressive, Does.Not.Contain("ship.MoveToPoint(target.GetPosition());"));
        }
    }
}
