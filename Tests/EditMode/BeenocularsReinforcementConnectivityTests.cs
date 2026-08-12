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
        public void MissionEightReinforcementsCannotRemainInSealedMapRegions()
        {
            string shared = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));
            string routing = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Titania2ReinforcementRouting.cs"));

            Assert.That(shared, Does.Contain("== 8)"));
            Assert.That(shared, Does.Contain("EnsureTitania2ReinforcementRoute(ref startingPosition, ref nextPosition);"));
            Assert.That(routing, Does.Contain("AreStaticallyConnected(nextPosition, Titania2Center, ConfigData.MinimumClearance)"));
            Assert.That(routing, Does.Contain("AreStaticallyConnected(entryPoint, Titania2Center, ConfigData.MinimumClearance)"));
            Assert.That(routing, Does.Contain("Beenoculars reinforcement entry"));
            Assert.That(routing, Does.Contain("no connected map-edge reinforcement lane"),
                "If no connected edge exists, the mission must fail safe inside the playable arena instead of stranding Bees.");
        }
    }
}
