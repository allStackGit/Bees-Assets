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

            Assert.That(interaction, Does.Contain("private static int _lastEnemyRightClickFrame = -1;"));
            Assert.That(interaction, Does.Contain("_lastEnemyRightClickFrame == Time.frameCount"));
            Assert.That(interaction, Does.Not.Contain("AreStaticallyConnected("),
                "Right-click input must not lazily flood-fill the pathfinder grid on Unity's main thread.");
            Assert.That(interaction, Does.Contain("selectedSquad.UserAggressive(Squad)"));

            Assert.That(aggressive, Does.Contain("if (!ship.IsPathfinding)"),
                "Recurring aggressive targeting must not invalidate a live A* request every command tick.");
            Assert.That(aggressive, Does.Contain("ship.MoveToPoint(target.GetPosition());"));
        }
    }
}
