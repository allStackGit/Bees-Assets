using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TrainingOutcomeAttributionTests
    {
        [Test]
        public void TargetlessCommandsDoNotTrainShootingOrTargetingStrategies()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            int storeCommands = source.IndexOf("public void StoreCommands()", StringComparison.Ordinal);
            Assert.That(storeCommands, Is.GreaterThanOrEqualTo(0));

            int shootingGuard = source.IndexOf("else if (command.HasTargetingEnemy)", storeCommands, StringComparison.Ordinal);
            int addShooting = source.IndexOf("_shootingCommands.Add(command);", storeCommands, StringComparison.Ordinal);
            int targetingGuard = source.IndexOf("command.MatchupStrategy != null && command.HasTargetingEnemy", storeCommands, StringComparison.Ordinal);
            int addTargeting = source.IndexOf("_targetingCommands.Add(command);", storeCommands, StringComparison.Ordinal);

            Assert.That(shootingGuard, Is.GreaterThan(storeCommands));
            Assert.That(addShooting, Is.GreaterThan(shootingGuard));
            Assert.That(targetingGuard, Is.GreaterThan(addShooting));
            Assert.That(addTargeting, Is.GreaterThan(targetingGuard));
        }

        [Test]
        public void StoredCommandSnapshotsWhetherAnEnemyWasSelected()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "StoredCommand.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public bool HasTargetingEnemy;", source);
            StringAssert.Contains("HasTargetingEnemy = command.EnemySquad != null;", source);
        }
    }
}
