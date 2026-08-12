using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class BeenocularsReinforcementConnectivityTests
    {
        [Test]
        public void MissionEightReinforcementsEnterFromOffscreenThroughConnectedEntries()
        {
            string shared = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));
            string routing = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Titania2ReinforcementRouting.cs"));
            string squad = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.OffscreenSpawn.cs"));

            Assert.That(shared, Does.Contain("EnsureTitania2ReinforcementRoute(ref startingPosition, ref nextPosition);"));
            Assert.That(routing, Does.Contain("AreStaticallyConnected(nextPosition, Titania2Center, ConfigData.MinimumClearance)"));
            Assert.That(routing, Does.Contain("startingPosition = GetTitania2OffscreenSpawn(nextPosition);"));
            Assert.That(routing, Does.Contain("startingPosition = GetTitania2OffscreenSpawn(bestEntry);"));
            Assert.That(routing, Does.Contain("nextPosition = bestEntry;"));

            Assert.That(shared, Does.Contain("new HashSet<Squad>(State.GetAllSquads())"));
            Assert.That(shared, Does.Contain("spawnedSquad.SetOffscreenStartingPosition(startingPosition);"));
            Assert.That(shared, Does.Contain("spawnedSquad.Move(nextPosition);"));
            Assert.That(squad, Does.Contain("SetOffscreenStartingPosition(Vector2 center)"));
            Assert.That(squad, Does.Not.Contain("TryFindNearestValidDestination"),
                "Intentional off-screen placement must not run through obstacle-aware relocation.");
        }
    }
}
