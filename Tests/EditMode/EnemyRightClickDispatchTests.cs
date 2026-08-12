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
        public void EnemyRightClickIsDeduplicatedAndRejectsDisconnectedTargetsBeforeCommandSetup()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Interaction.cs"));

            Assert.That(source, Does.Contain("private static int _lastEnemyRightClickFrame = -1;"));
            Assert.That(source, Does.Contain("private static int _lastEnemyRightClickSquadItemId = int.MinValue;"));
            Assert.That(source, Does.Contain("_lastEnemyRightClickFrame == Time.frameCount"));
            Assert.That(source, Does.Contain("_lastEnemyRightClickSquadItemId == targetSquadItemId"));
            Assert.That(source, Does.Contain("private static bool CanIssueUserAttack(Squad attacker, Squad enemy)"));
            Assert.That(source, Does.Contain("AreStaticallyConnected(attacker.GetPosition(), enemy.GetPosition(), clearance)"));
            Assert.That(source, Does.Contain("if (!CanIssueUserAttack(selectedSquad, Squad))"));
            Assert.That(source, Does.Contain("selectedSquad.UserAggressive(Squad);"));
        }
    }
}
