using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignTriggerStructureTests
    {
        private string _levelsFolder;

        [SetUp]
        public void SetUp()
        {
            _levelsFolder = Path.Combine(Application.dataPath, "Scripts", "Levels");
        }

        [Test]
        public void LegacyCampaignTriggerFileIsOnlyACompatibilityStub()
        {
            string legacy = Read("LeveLTriggers.cs");
            StringAssert.Contains("Compatibility stub", legacy);
            StringAssert.DoesNotContain("Pluto1Anomaly", legacy);
            StringAssert.DoesNotContain("Neptune2OfProduction", legacy);
            StringAssert.DoesNotContain("Uranus2OnTheDefensive", legacy);
            StringAssert.DoesNotContain("Titania1Minesweeper", legacy);
            StringAssert.DoesNotContain("Titania2Beenoculars", legacy);
        }

        [Test]
        public void CampaignMissionsAreSplitByOwnership()
        {
            AssertPartial("Level.Campaign.Shared.cs", "private void SetTriggers", "public void CloseLevel", "public void AddReinforcementSquads");
            AssertPartial("Level.Campaign.Pluto.cs", "public void Pluto1Anomaly", "public void Pluto4BluerPastures");
            AssertPartial("Level.Campaign.Neptune.cs", "public void Neptune1SeizeTheMeans", "public void Neptune2OfProduction");
            AssertPartial("Level.Campaign.Uranus1.cs", "public void Uranus1OnTheOffensive", "public void SelectedCarrierTrigger");
            AssertPartial("Level.Campaign.Uranus2.cs", "public void Uranus2OnTheDefensive", "public void SetRetreatForUranus2");
            AssertPartial("Level.Campaign.Uranus3.cs", "public void Uranus3ANewThreat");
            AssertPartial("Level.Campaign.Endings.cs", "public void Pluto1Ending", "public void Uranus3Ending");
        }

        [Test]
        public void PlutoScoutTooltipUsesSingleOwnedReference()
        {
            string pluto = Read("Level.Campaign.Pluto.cs");
            StringAssert.Contains("moveScoutTooltip = Instantiate", pluto);
            StringAssert.DoesNotContain("Tooltip moveScoutTooltip = Instantiate", pluto);
        }

        [Test]
        public void Uranus3HiveMindStartupDoesNotRequireCarrierTutorial()
        {
            string uranus3 = Read("Level.Campaign.Uranus3.cs");

            StringAssert.Contains(
                "bool hasCarrierInLevel = State.GetHumanShipTypes().Contains(ConfigData.ShipTypes.Carrier);",
                uranus3);
            StringAssert.Contains("Level 11 HiveMind activation without Carrier", uranus3);
            StringAssert.Contains("FinishCarrierIntroduction,", uranus3);
        }

        [Test]
        public void SupersededMissionImplementationsAreNotCarriedForward()
        {
            string combined = Read("Level.Campaign.Pluto.cs") + Read("Level.Campaign.Neptune.cs") +
                Read("Level.Campaign.Uranus1.cs") + Read("Level.Campaign.Uranus2.cs") +
                Read("Level.Campaign.Uranus3.cs") + Read("Level.Campaign.Endings.cs");

            StringAssert.DoesNotContain("public void Neptune3PressingForward()", combined);
            StringAssert.DoesNotContain("public void Titania1Minesweeper()", combined);
            StringAssert.DoesNotContain("public void Titania2Beenoculars()", combined);
            StringAssert.DoesNotContain("public void Titania1Ending()", combined);
            StringAssert.DoesNotContain("public void Titania2Ending()", combined);

            string catalog = Read("CampaignMissionCatalog.cs");
            StringAssert.Contains("nameof(Level.Neptune3PressingForwardCampaign)", catalog);
            StringAssert.Contains("nameof(Level.Titania1MinesweeperCampaign)", catalog);
            StringAssert.Contains("nameof(Level.Titania2BeenocularsCampaign)", catalog);
        }

        private string Read(string filename)
        {
            return File.ReadAllText(Path.Combine(_levelsFolder, filename));
        }

        private void AssertPartial(string filename, params string[] markers)
        {
            string source = Read(filename);
            StringAssert.Contains("public partial class Level", source);
            foreach (string marker in markers)
            {
                StringAssert.Contains(marker, source);
            }
        }
    }
}
