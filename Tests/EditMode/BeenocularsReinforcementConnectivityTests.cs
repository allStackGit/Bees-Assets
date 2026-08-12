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
        public void MissionEightReinforcementsSpawnOnlyAtConnectedPlayableEntries()
        {
            string shared = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));
            string routing = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Titania2ReinforcementRouting.cs"));

            Assert.That(shared, Does.Contain("== 8)"));
            Assert.That(shared, Does.Contain("EnsureTitania2ReinforcementRoute(ref startingPosition, ref nextPosition);"));
            Assert.That(routing, Does.Contain("AreStaticallyConnected(nextPosition, Titania2Center, ConfigData.MinimumClearance)"));
            Assert.That(routing, Does.Contain("startingPosition = nextPosition;"),
                "A connected Beenoculars entry must also become the actual spawn position; off-map spawn coordinates can be relocated into sealed pockets by SetStartingPosition().");
            Assert.That(routing, Does.Contain("AreStaticallyConnected(entryPoint, Titania2Center, ConfigData.MinimumClearance)"));
            Assert.That(routing, Does.Contain("startingPosition = bestEntry;"));
            Assert.That(routing, Does.Contain("nextPosition = bestEntry;"));
            Assert.That(routing, Does.Not.Contain("bestSpawn"),
                "Beenoculars must not retain an off-map spawn point after selecting a connected entry.");
            Assert.That(routing, Does.Contain("no connected map-edge reinforcement lane"),
                "If no connected edge exists, the mission must fail safe inside the playable arena instead of stranding Bees.");
        }
    }
}
