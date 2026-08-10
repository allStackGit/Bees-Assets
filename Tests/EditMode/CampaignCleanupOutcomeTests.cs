using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignCleanupOutcomeTests
    {
        [Test]
        public void CloseLevelSnapshotsEliminationBeforeKillingSurvivors()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));

            int closeLevel = source.IndexOf("public void CloseLevel()", StringComparison.Ordinal);
            int snapshot = source.IndexOf("State.CaptureEliminationState();", closeLevel, StringComparison.Ordinal);
            int cleanup = source.IndexOf("foreach (Ship ship in State.GetShips().ToList())", closeLevel, StringComparison.Ordinal);

            Assert.That(snapshot, Is.GreaterThan(closeLevel));
            Assert.That(cleanup, Is.GreaterThan(snapshot));
        }

        [Test]
        public void IsSideKilledUsesCapturedOutcomeAndResetClearsIt()
        {
            string queries = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.Queries.cs"));
            string state = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.cs"));

            StringAssert.Contains("TryGetCapturedEliminationState(side, out bool capturedState)", queries);
            StringAssert.Contains("_hasEliminationSnapshot = false;", state);
            StringAssert.Contains("_eliminationSnapshot[0] = false;", state);
            StringAssert.Contains("_eliminationSnapshot[1] = false;", state);
        }

        [Test]
        public void ExplicitMissionWinnerOverridesRawShipEliminationSnapshot()
        {
            string state = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "GameState.cs"));

            int capture = state.IndexOf("public void CaptureEliminationState()", StringComparison.Ordinal);
            int winnerCheck = state.IndexOf("Level.WinningSide == ConfigData.Configuration.HumanSide", capture, StringComparison.Ordinal);
            int resolvedOutcome = state.IndexOf("_eliminationSnapshot[side - 1] = side != Level.WinningSide;", winnerCheck, StringComparison.Ordinal);
            int rawShipFallback = state.IndexOf("List<Ship> sideShips = GetShips(side);", resolvedOutcome, StringComparison.Ordinal);

            Assert.That(winnerCheck, Is.GreaterThan(capture));
            Assert.That(resolvedOutcome, Is.GreaterThan(winnerCheck));
            Assert.That(rawShipFallback, Is.GreaterThan(resolvedOutcome));
        }
    }
}
