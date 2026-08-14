using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RandomSquadSetupPerformanceTests
    {
        [Test]
        public void LevelSetupBypassesLegacyRandomSquadAllocator()
        {
            string setup = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Setup.cs"));
            string randomSetup = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.RandomSquadSetup.cs"));

            StringAssert.Contains("SetupShipsForSide(ConfigData.Configuration.AISide);", setup);
            StringAssert.Contains("SetupShipsForSide(ConfigData.Configuration.UserSide);", setup);
            StringAssert.DoesNotContain("LevelConstructor.SetupShips(", setup);

            StringAssert.Contains("private readonly List<SavedSquad> _randomSquadBuffer", randomSetup);
            StringAssert.Contains("_randomSquadBuffer.Clear();", randomSetup);
            StringAssert.Contains("bool noVisibleArmedTypes = HasNoVisibleArmedTypes(side);", randomSetup);
            StringAssert.DoesNotContain(".Intersect(", randomSetup);
            StringAssert.DoesNotContain(".ElementAt(", randomSetup);
        }

        [Test]
        public void HumanGenerationPreservesLegacyRandomDrawOrder()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.RandomSquadSetup.cs"));

            int beeDraw = source.IndexOf("ConfigData.ShipTypes type = Stage.BeeShipTypes[Random.Range(0, Stage.BeeShipTypes.Count)]");
            int humanBranch = source.IndexOf("if (side == ConfigData.Configuration.HumanSide)", beeDraw);
            int humanDraw = source.IndexOf("type = Stage.HumanShipTypes[Random.Range(0, Stage.HumanShipTypes.Count)]", humanBranch);

            Assert.That(beeDraw, Is.GreaterThanOrEqualTo(0));
            Assert.That(humanBranch, Is.GreaterThan(beeDraw));
            Assert.That(humanDraw, Is.GreaterThan(humanBranch));
        }

        [Test]
        public void SideSetupPreservesLegacyOverrideAndExistingSquadBranches()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.RandomSquadSetup.cs"));

            StringAssert.Contains("Stage.UseOverrideSquads && side == ConfigData.Configuration.UserSide", source);
            StringAssert.Contains("Stage.UseOverrideEnemySquads && side == ConfigData.Configuration.AISide", source);
            StringAssert.Contains("CurrentLevelOptions.EnemyExistingSquads", source);
            StringAssert.Contains("LevelConstructor.AddOverrideSquads(side);", source);
            StringAssert.Contains("LevelConstructor.SpawnShipsAndSquads(", source);
        }

        [Test]
        public void SpawnAndFormationSetupReuseOwnedBuffers()
        {
            string constructor = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "LevelConstructor.cs"));
            string squad = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.cs"));

            StringAssert.Contains("private readonly List<Squad> _setupSquads", constructor);
            StringAssert.Contains("private readonly List<Ship> _carriers", constructor);
            StringAssert.Contains("private readonly List<SquadShip> _liveSquadShips", constructor);
            StringAssert.Contains("_setupSquads.Clear();", constructor);
            StringAssert.Contains("_carriers.Clear();", constructor);
            StringAssert.Contains("_liveSquadShips.Clear();", constructor);
            StringAssert.DoesNotContain(".Where((s) => !s.GetFleetShip().IsDead).ToList()", constructor);

            StringAssert.Contains("private readonly List<(Ship ship, Vector2 position)> _placedFormationSlots", squad);
            StringAssert.Contains("_placedFormationSlots.Clear();", squad);
            StringAssert.Contains("_placedFormationSlots.Add((ship, position));", squad);
            StringAssert.DoesNotContain("List<(Ship ship, Vector2 position)> placed = new", squad);
            StringAssert.DoesNotContain("GetShips().ForEach(ship =>", squad);
        }
    }
}
