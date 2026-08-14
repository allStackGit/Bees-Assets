using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignDialogueDocumentSyncTests
    {
        [Test]
        public void CurrentCampaignDialogueCarriesMissionScriptingRevisions()
        {
            string source = Read("Scripts", "CampaignDialogueOverrides.cs");

            StringAssert.Contains("Gunship P-4 reporting to command", source);
            StringAssert.Contains("United Fleet airspace", source);
            StringAssert.Contains("Scouts also come loaded up with five beacons", source);
            StringAssert.Contains("Pluto’s full fleet online", source);
            StringAssert.Contains("Heavily.", source);
            StringAssert.Contains("Its weapon has a huge range", source);
            StringAssert.Contains("you need to know how the carrier works", source);
        }

        [Test]
        public void EveryCampaignDialogueSectionIsPatchedImmediatelyBeforeDisplay()
        {
            string manager = Read("Scripts", "UI Components", "DialogueManager.cs");
            string overrides = Read("Scripts", "CampaignDialogueOverrides.cs");

            StringAssert.Contains("CampaignDialogueOverrides.Apply(CutsceneManager);", manager);
            StringAssert.Contains("ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign", manager);
            StringAssert.Contains("CampaignDialogueOverrideGuard", overrides);
            StringAssert.Contains("guard.enabled = false", manager);
        }

        [Test]
        public void AmiUranusDialogueDependsOnPersistedBeenocularsOutcome()
        {
            string dialogue = Read("Scripts", "CampaignDialogueOverrides.cs");
            string state = Read("Scripts", "Levels", "TitaniaRouteState.cs");

            StringAssert.Contains("TitaniaRouteState.DidWinTitaniaTwo", dialogue);
            StringAssert.Contains("TitaniaTwoWon", state);
            StringAssert.Contains("RecordTitaniaTwoResult", state);
            StringAssert.Contains("TitaniaOutcomePersistenceGuard", state);
        }

        [Test]
        public void StruckPhilipExchangeIsRemovedRatherThanRewritten()
        {
            string source = Read("Scripts", "CampaignDialogueOverrides.cs");

            StringAssert.Contains("He blew up the weapons bay to surround the base in junk and experimental explosives", source);
            StringAssert.DoesNotContain("What about the engineering lead?", source);
            StringAssert.DoesNotContain("He took a Carrier out to draw their fire while we went dark", source);
        }

        [Test]
        public void CarrierIsAwardedAfterTitaniaNotNeptune()
        {
            string endings = Read("Scripts", "Levels", "Level.Campaign.Endings.cs");
            string titaniaState = Read("Scripts", "Levels", "TitaniaRouteState.cs");

            StringAssert.DoesNotContain("AddShipsToFleet(ConfigData.ShipTypes.Carrier, 1);", endings);
            StringAssert.Contains("AwardTitaniaCarrier", titaniaState);
            StringAssert.Contains("AddShipsToFleet(ConfigData.ShipTypes.Carrier, 1);", titaniaState);
            StringAssert.Contains("UnlockedCampaignShips.Add(ConfigData.ShipTypes.Carrier)", titaniaState);
        }

        [Test]
        public void NeptuneOneFailureSkipsMiningMission()
        {
            string endings = Read("Scripts", "Levels", "Level.Campaign.Endings.cs");

            StringAssert.Contains("ConfigData.UserProgressData.SetCurrentLevel(6);", endings);
        }

        [Test]
        public void UranusOneFailureSkipsDefensiveGameplayAndUsesItsPostMissionDialogue()
        {
            string endings = Read("Scripts", "Levels", "Level.Campaign.Endings.cs");
            string uranusOne = Read("Scripts", "Levels", "Level.Campaign.Uranus1.cs");

            StringAssert.Contains("ConfigData.UserProgressData.SetCurrentLevel(11);", endings);
            StringAssert.Contains("Uranus_OnTheDefensive.GetRange(14, 3)", uranusOne);
        }

        [Test]
        public void StruckUranusCruiserAndFritzSequenceIsNotRun()
        {
            string uranusOne = Read("Scripts", "Levels", "Level.Campaign.Uranus1.cs");
            string uranusTwo = Read("Scripts", "Levels", "Level.Campaign.Uranus2.cs");
            string uranusThree = Read("Scripts", "Levels", "Level.Campaign.Uranus3.cs");
            string endings = Read("Scripts", "Levels", "Level.Campaign.Endings.cs");

            StringAssert.DoesNotContain("ShipTypes.Cruiser", uranusOne);
            StringAssert.DoesNotContain("cruiserTimer", uranusOne);
            StringAssert.DoesNotContain("They’re… different.", uranusOne);
            StringAssert.DoesNotContain("GetRange(6, 6)", uranusOne);
            StringAssert.DoesNotContain("GetRange(18,", uranusOne);
            StringAssert.Contains("Uranus_OnTheOffensive.GetRange(35, 3)", uranusOne);
            StringAssert.DoesNotContain("UnlockedCampaignShips.Add(ConfigData.ShipTypes.Cruiser)", endings);

            // Legacy Fritz lines later in Uranus are only eligible if an older/future save really
            // contains a Cruiser; the current campaign route no longer creates one on Uranus I.
            StringAssert.Contains("HasShipsOfType(ConfigData.ShipTypes.Cruiser)", uranusTwo);
            StringAssert.Contains("HasShipsOfType(ConfigData.ShipTypes.Cruiser)", uranusThree);
        }

        [Test]
        public void RemovedEndMissionButtonIsNotReferencedByPlayableMiningMissions()
        {
            string neptune = Read("Scripts", "Levels", "Level.Campaign.Neptune.cs");
            string uranus = Read("Scripts", "Levels", "Level.Campaign.Uranus2.cs");

            StringAssert.DoesNotContain("End Mission", neptune);
            StringAssert.DoesNotContain("End Mission", uranus);
            StringAssert.Contains("green zone", neptune);
            StringAssert.Contains("green zone", uranus);
        }

        [Test]
        public void SaturnDialogueIsNotPretendedToBePlayableBeforeSaturnMissionsExist()
        {
            string catalog = Read("Scripts", "Levels", "CampaignMissionCatalog.cs");

            StringAssert.DoesNotContain("Saturn", catalog);
            StringAssert.Contains("new MissionDefinition(11, \"A New Threat\"", catalog);
        }

        private static string Read(params string[] parts)
        {
            string path = Application.dataPath;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path);
        }
    }
}
