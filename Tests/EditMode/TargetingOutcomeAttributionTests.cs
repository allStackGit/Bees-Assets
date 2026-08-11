using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TargetingOutcomeAttributionTests
    {
        [Test]
        public void StoredCommandSnapshotsWhetherATargetingEnemyWasSelected()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "StoredCommand.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public bool HasTargetingEnemy;", source);
            StringAssert.Contains("HasTargetingEnemy = command.EnemySquad != null;", source);
        }

        [Test]
        public void TargetingOutcomeIsStoredOnlyWhenAnEnemyWasActuallySelected()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("if (command.MatchupStrategy != null &&", source);
            StringAssert.Contains("command.HasTargetingEnemy &&", source);
            StringAssert.Contains("CommandUsesSelectedEnemy(command.CommandType))", source);
            StringAssert.Contains("_targetingCommands.Add(command);", source);
        }
    }
}
