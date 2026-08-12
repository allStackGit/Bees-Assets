using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class AggressiveReachabilityTests
    {
        [Test]
        public void AggressiveDoesNotSupersedeLivePathsOrBuildConnectivityOnClickFrame()
        {
            string aggressive = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Commands", "Aggressive.cs"));
            string interaction = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Interaction.cs"));

            Assert.That(aggressive, Does.Contain("if (!ship.IsPathfinding)"),
                "Recurring aggressive targeting must not invalidate an A* search that is still running.");
            Assert.That(aggressive, Does.Contain("ship.MoveToPoint(target.GetPosition());"));
            Assert.That(aggressive, Does.Match(@"if\s*\(IsHiveMindCommand\)\s*\{\s*PrepareDamageToSendEntries\(\);"),
                "User attacks must not do Hive Mind damage bookkeeping synchronously on the click frame.");

            Assert.That(interaction, Does.Not.Contain("AreStaticallyConnected("),
                "Enemy right-click handling must not synchronously build or query whole-map connectivity components.");
            Assert.That(interaction, Does.Contain("UserAggressive(Squad)"),
                "Enemy right-click handling must dispatch an aggressive command to the selected squads.");
        }
    }
}
