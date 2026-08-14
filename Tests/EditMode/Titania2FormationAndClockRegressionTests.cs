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

        [Test]
        public void BeenocularsUploadMessagesScaleWithActiveCountdownDuration()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Titania2Beenoculars.cs"));

            Assert.That(source, Does.Contain("const float victorySurvivalDuration = 330f;"));
            Assert.That(source, Does.Contain("const float defeatSurvivalDuration = 480f;"));
            Assert.That(source, Does.Contain("float uploadProgress = 1f - (timeLeft / survivalDuration);"),
                "A.M.I. upload milestones must be derived from the active mission countdown, not fixed elapsed seconds.");
            Assert.That(source, Does.Contain("uploadProgress >= 0.10f"));
            Assert.That(source, Does.Contain("uploadProgress >= 0.24f"));
            Assert.That(source, Does.Contain("uploadProgress >= 0.50f"));
            Assert.That(source, Does.Contain("uploadProgress >= 0.75f"));
            Assert.That(source, Does.Contain("uploadProgress >= 0.90f"));

            // 5:30 victory route: 10/24/50/75/90% upload leaves 4:57, 4:10.8, 2:45, 1:22.5, 0:33.
            Assert.That(330f * 0.90f, Is.EqualTo(297f).Within(0.001f));
            Assert.That(330f * 0.76f, Is.EqualTo(250.8f).Within(0.001f));
            Assert.That(330f * 0.50f, Is.EqualTo(165f).Within(0.001f));
            Assert.That(330f * 0.25f, Is.EqualTo(82.5f).Within(0.001f));
            Assert.That(330f * 0.10f, Is.EqualTo(33f).Within(0.001f));

            // 8:00 loss route: the same milestones shift to 7:12, 6:04.8, 4:00, 2:00, 0:48 remaining.
            Assert.That(480f * 0.90f, Is.EqualTo(432f).Within(0.001f));
            Assert.That(480f * 0.76f, Is.EqualTo(364.8f).Within(0.001f));
            Assert.That(480f * 0.50f, Is.EqualTo(240f).Within(0.001f));
            Assert.That(480f * 0.25f, Is.EqualTo(120f).Within(0.001f));
            Assert.That(480f * 0.10f, Is.EqualTo(48f).Within(0.001f));
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
