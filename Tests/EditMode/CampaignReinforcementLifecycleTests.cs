using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesCampaignScenario")]
    public class CampaignReinforcementLifecycleTests
    {
        [Test]
        public void CampaignReinforcementsIgnoreDeadPersistedShips()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public void AddReinforcementSquads", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            string method = source.Substring(start);

            Assert.That(method, Does.Contain("squad.GetAliveSquadShips().Count > 0"),
                "Dead-only persisted squads must not be converted into runtime reinforcement squads.");
            Assert.That(method, Does.Contain("List<SquadShip> aliveShips = squads[i].GetAliveSquadShips()"));
            Assert.That(method, Does.Contain("aliveShips[0].ShipType"),
                "Replacement composition must be derived from a living ship, not a dead persisted entry.");
            Assert.That(method, Does.Contain("aliveShips.Count"),
                "Replacement squad size must describe the living composition.");
            Assert.That(method, Does.Not.Contain("squads[i].GetSquadShips()[0].ShipType"));
        }
    }
}
