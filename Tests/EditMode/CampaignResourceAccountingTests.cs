using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignResourceAccountingTests
    {
        [Test]
        public void PlayerMiningCollectionIgnoresNonUserSquads()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Endings.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf("private void CollectMinedMineralsForPlayer()");
            int methodEnd = source.IndexOf("private void SaveCampaignProgress()", methodStart);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, methodEnd - methodStart);
            StringAssert.Contains("_save_savedSquad.Side != ConfigData.Configuration.UserSide", method);
            StringAssert.Contains("continue;", method);
            StringAssert.Contains("State.PlayerMineralsReceived += _save_fleetship.MineralsMinedThisLevel", method);
        }

        [Test]
        public void CampaignOutcomeBranchesAdvanceExactlyOnceAfterOutcomeAccounting()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Endings.cs");
            string source = File.ReadAllText(path);

            string neptune1 = ExtractMethodBody(source, "Neptune1Ending");
            int mineralsAccounting = neptune1.IndexOf("int mineralsMined = 0;", StringComparison.Ordinal);
            int completionAdvance = neptune1.IndexOf(
                "ConfigData.UserProgressData.AdvanceToNextLevel();",
                StringComparison.Ordinal);
            int secondAdvance = neptune1.IndexOf(
                "ConfigData.UserProgressData.AdvanceToNextLevel();",
                completionAdvance + 1,
                StringComparison.Ordinal);

            Assert.That(neptune1, Does.Contain("WinningSide == ConfigData.Configuration.UserSide"));
            Assert.That(mineralsAccounting, Is.GreaterThanOrEqualTo(0));
            Assert.That(completionAdvance, Is.GreaterThan(mineralsAccounting));
            Assert.That(secondAdvance, Is.EqualTo(-1),
                "Neptune 1 must advance exactly once; the former loss-path advance skipped Neptune 2.");

            string uranus1 = ExtractMethodBody(source, "Uranus1Ending");
            int uranusAdvance = uranus1.IndexOf(
                "ConfigData.UserProgressData.AdvanceToNextLevel();",
                StringComparison.Ordinal);
            int uranusSecondAdvance = uranus1.IndexOf(
                "ConfigData.UserProgressData.AdvanceToNextLevel();",
                uranusAdvance + 1,
                StringComparison.Ordinal);

            Assert.That(uranusAdvance, Is.GreaterThanOrEqualTo(0));
            Assert.That(uranusSecondAdvance, Is.EqualTo(-1),
                "Uranus 1 must advance exactly once and must not skip Uranus 2 on a loss or missing Factory.");
            Assert.That(uranus1, Does.Not.Contain(
                "WinningSide == ConfigData.Configuration.AISide || !ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Factory)"));
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            Assert.That(openingBrace, Is.GreaterThanOrEqualTo(0));

            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}' && --depth == 0)
                {
                    return source.Substring(openingBrace, index - openingBrace + 1);
                }
            }

            Assert.Fail($"Method {methodName} has no balanced body.");
            return string.Empty;
        }
    }
}
