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
        public void RebuildingCampaignTriggersDiscardsDeferredTriggersFromThePreviousLevel()
        {
            string shared = Read("Level.Campaign.Shared.cs");
            int start = shared.IndexOf("private void SetTriggers()");
            int end = shared.IndexOf("public void EasterEggTriggers()", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string rebuild = shared.Substring(start, end - start);

            StringAssert.Contains("Triggers.Clear();", rebuild);
            StringAssert.Contains("NextTriggers.Clear();", rebuild);
            Assert.That(
                rebuild.IndexOf("NextTriggers.Clear();"),
                Is.GreaterThan(rebuild.IndexOf("Triggers.Clear();")));
        }

        [Test]
        public void CampaignProcessingStopsAfterLevelClosure()
        {
            string runtime = Read("Level.Runtime.cs");

            int triggerStart = runtime.IndexOf("internal int EvaluateCampaignTriggers()");
            int timerStart = runtime.IndexOf("public void UpdateTimers()");
            int timerEnd = runtime.IndexOf("private ScaledTimer _saveAndEndHalfSecond", timerStart);
            Assert.That(triggerStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(timerStart, Is.GreaterThan(triggerStart));
            Assert.That(timerEnd, Is.GreaterThan(timerStart));

            string triggerLoop = runtime.Substring(triggerStart, timerStart - triggerStart);
            string timerLoop = runtime.Substring(timerStart, timerEnd - timerStart);
            int triggerAction = triggerLoop.IndexOf("trigger.Action();");
            int triggerCloseGuard = triggerLoop.IndexOf("if (!IsLevelConnectedToServer)", triggerAction);
            int timerCallback = timerLoop.IndexOf("timer.Update()");
            int timerCloseGuard = timerLoop.IndexOf("if (!IsLevelConnectedToServer)", timerCallback);

            Assert.That(triggerAction, Is.GreaterThanOrEqualTo(0));
            Assert.That(triggerCloseGuard, Is.GreaterThan(triggerAction));
            Assert.That(timerCallback, Is.GreaterThanOrEqualTo(0));
            Assert.That(timerCloseGuard, Is.GreaterThan(timerCallback));
            StringAssert.Contains("break;", triggerLoop.Substring(triggerCloseGuard));
            StringAssert.Contains("break;", timerLoop.Substring(timerCloseGuard));
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
        public void PlutoOneResolvesMissionShipsByConfiguredSideAndType()
        {
            string pluto = Read("Level.Campaign.Pluto.cs");
            int start = pluto.IndexOf("public void Pluto1Anomaly");
            int end = pluto.IndexOf("public void Pluto2Reinforcements", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string mission = pluto.Substring(start, end - start);

            StringAssert.Contains(
                "State.GetShips(ConfigData.Configuration.UserSide).First(ship => ship.ShipType == ConfigData.ShipTypes.Scout)",
                mission);
            StringAssert.Contains(
                "State.GetShips(ConfigData.Configuration.AISide).First(ship => ship.ShipType == ConfigData.ShipTypes.Honeybee)",
                mission);
            StringAssert.Contains(
                "State.GetShips(ConfigData.Configuration.UserSide).First(ship => ship.ShipType == ConfigData.ShipTypes.Gunship)",
                mission);
            StringAssert.DoesNotContain("State.GetHumanShips()", mission);
            StringAssert.DoesNotContain("State.GetBeeShips()", mission);
        }

        [Test]
        public void PlutoFourFleetTutorialUsesConfiguredUserSide()
        {
            string mission = Read("Level.Campaign.Pluto4.cs");
            StringAssert.Contains("HashSet<ShipTypes> shipTypes = State.GetUserShipTypes();", mission);
            StringAssert.DoesNotContain("State.GetHumanShipTypes()", mission);

            string legacy = Read("Level.Campaign.Pluto.cs");
            int legacyMission = legacy.IndexOf("public void Pluto4BluerPastures()");
            Assert.That(legacyMission, Is.GreaterThanOrEqualTo(0));
            string legacyMissionBody = legacy.Substring(legacyMission);
            StringAssert.Contains("HashSet<ShipTypes> shipTypes = State.GetUserShipTypes();", legacyMissionBody);
            StringAssert.DoesNotContain("State.GetHumanShipTypes()", legacyMissionBody);

            string queries = Read("GameState.Queries.cs");
            int start = queries.IndexOf("public HashSet<ConfigData.ShipTypes> GetUserShipTypes()");
            int end = queries.IndexOf("public List<Ship> GetBeeShips()", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            StringAssert.Contains(
                "return GetShipTypes(ConfigData.Configuration.UserSide);",
                queries.Substring(start, end - start));
        }

        [Test]
        public void UranusCarrierTutorialGateUsesTheConfiguredUserFleet()
        {
            string uranus1 = Read("Level.Campaign.Uranus1.cs");
            int start = uranus1.IndexOf("public void SelectedCarrierTrigger()");
            int end = uranus1.IndexOf("private void FinishCarrierIntroduction", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string method = uranus1.Substring(start, end - start);

            StringAssert.Contains(
                "State.GetUserShipTypes().Contains(ConfigData.ShipTypes.Carrier)",
                method);
            StringAssert.Contains(
                "State.GetSquadsBySide(ConfigData.Configuration.UserSide)",
                method);
            StringAssert.DoesNotContain("State.GetHumanShipTypes()", method);
        }

        [Test]
        public void UranusOneProximitySensingIsAttachedToUserScouts()
        {
            string uranus1 = Read("Level.Campaign.Uranus1.cs");
            int start = uranus1.IndexOf("public void Uranus1OnTheOffensive()");
            int end = uranus1.IndexOf("public void SelectedCarrierTrigger()", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string mission = uranus1.Substring(start, end - start);

            StringAssert.Contains(
                "State.GetShips(ConfigData.Configuration.UserSide).ForEach(ship =>",
                mission);
            StringAssert.DoesNotContain("State.GetHumanShips()", mission);
        }

        [Test]
        public void Uranus3HiveMindStartupDoesNotRequireCarrierTutorial()
        {
            string uranus3 = Read("Level.Campaign.Uranus3.cs");

            StringAssert.Contains(
                "bool hasCarrierInLevel = State.GetUserShipTypes().Contains(ConfigData.ShipTypes.Carrier);",
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
