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
            StringAssert.Contains("They’re… different.", source);
            StringAssert.Contains("you need to know how the carrier works", source);
        }

        [Test]
        public void LiveCampaignStagesReceiveTheSameOverridesAsLevelIntros()
        {
            string source = Read("Scripts", "CampaignDialogueOverrides.cs");

            StringAssert.Contains("CampaignDialogueOverrideGuard", source);
            StringAssert.Contains("CampaignDialogueOverrides.Apply(manager);", source);
            StringAssert.Contains("ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign", source);
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
        public void PhilipBackstoryDoesNotReintroduceConflictingCarrierDeath()
        {
            string source = Read("Scripts", "CampaignDialogueOverrides.cs");

            StringAssert.Contains("destroyed Titania’s weapons bay to create the debris field", source);
            StringAssert.DoesNotContain("He took a Carrier out to draw their fire while we went dark", source);
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
