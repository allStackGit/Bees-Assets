using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TitaniaCampaignTransitionTests
    {
        [Test]
        public void BeenocularsEndingRestoresPlanetIntermissionStateBeforeAdvancing()
        {
            string source = ReadBeenocularsSource();
            string endingSource = ExtractMethodBody(source, "public void Titania2CampaignEnding()");

            int resetIntro = endingSource.IndexOf("ConfigData.HasSeenPreLevelIntro = false;", StringComparison.Ordinal);
            int resetIntermission = endingSource.IndexOf("ConfigData.HasSeenIntermission = false;", StringComparison.Ordinal);
            int advance = endingSource.IndexOf("ConfigData.UserProgressData.AdvanceToNextLevel();", StringComparison.Ordinal);

            Assert.That(resetIntro, Is.GreaterThanOrEqualTo(0));
            Assert.That(resetIntermission, Is.GreaterThanOrEqualTo(0));
            Assert.That(advance, Is.GreaterThan(resetIntro));
            Assert.That(advance, Is.GreaterThan(resetIntermission));
        }

        [Test]
        public void BeenocularsDefeatAlsoAdvancesToUranus()
        {
            string source = ReadBeenocularsSource();
            string endingSource = ExtractMethodBody(source, "public void Titania2CampaignEnding()");

            int victoryCondition = endingSource.IndexOf(
                "if (WinningSide == ConfigData.Configuration.UserSide)",
                StringComparison.Ordinal);
            Assert.That(victoryCondition, Is.GreaterThanOrEqualTo(0));

            int victoryBlockStart = endingSource.IndexOf('{', victoryCondition);
            int victoryBlockEnd = FindMatchingBrace(endingSource, victoryBlockStart);
            int advance = endingSource.IndexOf(
                "ConfigData.UserProgressData.AdvanceToNextLevel();",
                StringComparison.Ordinal);
            int checkpoint = endingSource.IndexOf("CampaignCheckpoint.Save();", StringComparison.Ordinal);

            Assert.That(advance, Is.GreaterThan(victoryBlockEnd),
                "Titania II must fail forward to Uranus even when WinningSide is the AI.");
            Assert.That(checkpoint, Is.GreaterThan(advance),
                "The fail-forward campaign level must be checkpointed after advancing.");
        }

        [Test]
        public void GeneralCampaignAdvancementDoesNotVetoBeenocularsDefeat()
        {
            string progressSource = ReadProgressSource();
            string advanceSource = ExtractMethodBody(progressSource, "public void AdvanceToNextLevel()");

            StringAssert.DoesNotContain("Beenoculars was lost", advanceSource);
            StringAssert.DoesNotContain("missionId == 8 && activeLevel.WinningSide", advanceSource);
            StringAssert.Contains("int targetLevel = missionId + 1;", advanceSource);
            StringAssert.Contains("SetCurrentLevel(targetLevel);", advanceSource);
        }

        [Test]
        public void CampaignAdvancementStopsAtCatalogCompletionSentinel()
        {
            string progressSource = ReadProgressSource();
            string advanceSource = ExtractMethodBody(progressSource, "public void AdvanceToNextLevel()");

            StringAssert.Contains("CampaignMissionCatalog.IsCampaignComplete(targetLevel)", advanceSource);
            StringAssert.Contains("CampaignMissionCatalog.IsCampaignComplete(fallbackTargetLevel)", advanceSource);
            StringAssert.Contains("currently available campaign", advanceSource);
            StringAssert.Contains("terminal level", advanceSource);
        }

        private static string ReadBeenocularsSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "Titania2Beenoculars.cs"));
        }

        private static string ReadProgressSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Data",
                "UserProgressData.cs"));
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int method = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(method, Is.GreaterThanOrEqualTo(0));
            int openingBrace = source.IndexOf('{', method);
            int closingBrace = FindMatchingBrace(source, openingBrace);
            return source.Substring(openingBrace, closingBrace - openingBrace + 1);
        }

        private static int FindMatchingBrace(string source, int openingBrace)
        {
            Assert.That(openingBrace, Is.GreaterThanOrEqualTo(0));
            int depth = 0;
            for (int i = openingBrace; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}' && --depth == 0)
                {
                    return i;
                }
            }

            Assert.Fail("Could not find matching closing brace.");
            return -1;
        }
    }
}
