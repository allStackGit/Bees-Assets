using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class FeedbackAdjustmentContractTests
    {
        [Test]
        public void DialoguePortraitCyclingDoesNotSpecialCaseSamuel()
        {
            string source = ReadSource("Scripts", "UI Components", "DialogueManager.cs");
            string typeLine = ExtractMethodBody(source, "IEnumerator TypeLine");

            Assert.That(typeLine, Does.Contain("SetPortrait(line.PortraitB)"));
            Assert.That(typeLine, Does.Not.Contain("line.SpeakerName"),
                "Samuel and every other speaking character should use the normal authored portrait cycle.");
        }

        [Test]
        public void MissionStatusAnimationPreservesExactAuthoredLayoutTarget()
        {
            string source = ReadSource("Scripts", "UI Components", "MissionStatusIntroMotionGuard.cs");

            Assert.That(source, Does.Contain("_targetWorldPosition = _statusRect.position"));
            Assert.That(source, Does.Contain("_statusRect.GetWorldCorners(statusCorners)"));
            Assert.That(source, Does.Contain("canvasRect.GetWorldCorners(canvasCorners)"));
            Assert.That(source, Does.Contain("_statusRect.position = _targetWorldPosition"));
            Assert.That(source, Does.Not.Contain("anchorMin.y = 1f"));
            Assert.That(source, Does.Not.Contain("_targetPosition.y = 0f"));
        }

        [Test]
        public void PlutoFourObjectiveAndReadinessDialogueReflectAvailableShipTypes()
        {
            string source = ReadSource("Scripts", "Levels", "Level.Campaign.Pluto4.cs");

            Assert.That(source, Does.Contain("SetMissionStatus(\"Defend Pluto and Survive!\")"));
            Assert.That(source, Does.Contain("shipTypes.Contains(ConfigData.ShipTypes.Dreadnought)"));
            Assert.That(source, Does.Contain("shipTypes.Contains(ConfigData.ShipTypes.Gunship)"));
            Assert.That(source, Does.Contain("shipTypes.Contains(ConfigData.ShipTypes.Frigate)"));
            Assert.That(source, Does.Contain("shipTypes.Contains(ConfigData.ShipTypes.Scout)"));
            Assert.That(source, Does.Contain("PlutoLines_BluerPastures[10]"));
            Assert.That(source, Does.Contain("GetRange(11, 2)"));
        }

        [Test]
        public void SquadMakerFeedbackRepairsDeletedSquadMembershipBeforeRefillingSquads()
        {
            string source = ReadSource("Scripts", "UI Components", "SquadMakerFeedbackAdjustmentGuard.cs");
            int repair = source.IndexOf("private bool RepairOrphanedSquadMembership()", StringComparison.Ordinal);
            Assert.That(repair, Is.GreaterThanOrEqualTo(0));

            int release = source.IndexOf("ship.DoesBelongToSavedSquad = false", repair, StringComparison.Ordinal);
            int replace = source.IndexOf("ReplaceDeadSquadShips(false)", release, StringComparison.Ordinal);
            Assert.That(release, Is.GreaterThan(repair));
            Assert.That(replace, Is.GreaterThan(release),
                "Ships released by deleting a squad must become available before damaged squads are refilled.");
        }

        [Test]
        public void PlutoThreeOnlyDissolvesTheKnownUntouchedStarterSquads()
        {
            string source = ReadSource("Scripts", "UI Components", "SquadMakerFeedbackAdjustmentGuard.cs");

            Assert.That(source, Does.Contain("missionId != 2"));
            Assert.That(source, Does.Contain("squad.Stats.BattlesFought == 0"));
            Assert.That(source, Does.Contain("squad.Name.StartsWith(\"Squad #\""));
            Assert.That(source, Does.Contain("ContainsExactComposition(candidates, ConfigData.ShipTypes.Dreadnought, 3)"));
            Assert.That(source, Does.Contain("ContainsExactComposition(candidates, ConfigData.ShipTypes.Frigate, 3)"));
            Assert.That(source, Does.Contain("ContainsExactComposition(candidates, ConfigData.ShipTypes.Scout, 1)"));
        }

        [Test]
        public void TutorialPolishKeepsWidthContentDrivenHeightDoubleBorderAndWholeRowScrolling()
        {
            string source = ReadSource("Scripts", "UI Components", "TutorialFeedbackPolishGuard.cs");

            Assert.That(source, Does.Contain("GetMinimumTutorialHeight()"));
            Assert.That(source, Does.Contain("dialogueHeight * 0.5f"));
            Assert.That(source, Does.Contain("InnerBorderName"));
            Assert.That(source, Does.Contain("PutSentencesOnSeparateLines"));
            Assert.That(source, Does.Contain("GetComponentsInChildren<Graphic>(true)"));
            Assert.That(source, Does.Contain("graphicObject.AddComponent<SquadListScrollForwarder>()"));
        }

        private static string ExtractMethodBody(string source, string signatureText)
        {
            int signature = source.IndexOf(signatureText, StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find {signatureText}.");
            int openingBrace = source.IndexOf('{', signature);
            Assert.That(openingBrace, Is.GreaterThan(signature));

            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}' && --depth == 0)
                {
                    return source.Substring(openingBrace, index - openingBrace + 1);
                }
            }

            Assert.Fail($"Method {signatureText} has no balanced body.");
            return string.Empty;
        }

        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < pathParts.Length; i++)
            {
                path = Path.Combine(path, pathParts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}
