using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class Titania2FormationAndClockRegressionTests
    {
        [Test]
        public void BeenocularsStagesWholeSquadsInsteadOfRewritingIndividualShipOffsets()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Titania2Beenoculars.cs"));
            string staging = ExtractPrivateMethodBody(source, "StageTitania2HumanFleetAtCenter");

            Assert.That(staging, Does.Contain("squad.SetStartingPosition(placement);"),
                "Beenoculars should relocate the saved formation as a whole squad.");
            Assert.That(staging, Does.Not.Contain("ship.transform.localPosition"),
                "Mission staging must not scatter individual ships around Titania.");
            Assert.That(staging, Does.Not.Contain("squad.SetOffsets();"),
                "Mission staging must not replace authored formation offsets with temporary spawn positions.");
            Assert.That(source, Does.Contain("FindTitania2HumanSquadPlacement"));
        }

        [Test]
        public void BeenocularsInitializesClockTextBeforeMakingClockVisible()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Titania2Beenoculars.cs"));

            int initialText = source.IndexOf("clockText.text = $\"{initialMinutes}:{initialSeconds:D2}\";", StringComparison.Ordinal);
            int activateClock = source.IndexOf("Stage.Menus.Clock.SetActive(true);", StringComparison.Ordinal);

            Assert.That(initialText, Is.GreaterThanOrEqualTo(0));
            Assert.That(activateClock, Is.GreaterThan(initialText),
                "The authored prefab time must never render for a frame before the mission duration is initialized.");
        }

        private static string ExtractPrivateMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf("private void " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find private method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            Assert.That(openingBrace, Is.GreaterThanOrEqualTo(0));

            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}' && --depth == 0)
                    return source.Substring(openingBrace, index - openingBrace + 1);
            }

            Assert.Fail($"Method {methodName} has no balanced body.");
            return string.Empty;
        }
    }
}
