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

        [Test]
        public void DisconnectedCampaignLevelCompletesTeardownWithoutWaitingForStoppedTimers()
        {
            string runtime = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Runtime.cs"));

            int levelOver = runtime.IndexOf("public void LevelOver()", StringComparison.Ordinal);
            int disconnected = runtime.IndexOf("else if (!IsLevelConnectedToServer)", levelOver, StringComparison.Ordinal);
            int saveAndEnd = runtime.IndexOf("SaveAndEnd();", disconnected, StringComparison.Ordinal);

            Assert.That(disconnected, Is.GreaterThan(levelOver));
            Assert.That(saveAndEnd, Is.GreaterThan(disconnected),
                "A campaign level disconnected by CloseLevel would wait on a timer that Update no longer advances.");
        }

        [Test]
        public void CloseLevelStopsContinuousTriggersAndRemovesTargetingMarkers()
        {
            string shared = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));

            int closeLevel = shared.IndexOf("public void CloseLevel()", StringComparison.Ordinal);
            int cancelTriggerTimer = shared.IndexOf("CancelTimer(_checkTriggersTimer);", closeLevel, StringComparison.Ordinal);
            int disableContinuousTriggers = shared.IndexOf("HasContinuousTriggers = false;", cancelTriggerTimer, StringComparison.Ordinal);
            int removeTargetingMarkers = shared.IndexOf("State.TargetingSquadMarkers.ToList().ForEach(target => target.Kill());", disableContinuousTriggers, StringComparison.Ordinal);
            int killShips = shared.IndexOf("foreach (Ship ship in State.GetShips().ToList())", removeTargetingMarkers, StringComparison.Ordinal);

            Assert.That(cancelTriggerTimer, Is.GreaterThan(closeLevel));
            Assert.That(disableContinuousTriggers, Is.GreaterThan(cancelTriggerTimer));
            Assert.That(removeTargetingMarkers, Is.GreaterThan(disableContinuousTriggers));
            Assert.That(killShips, Is.GreaterThan(removeTargetingMarkers));
        }
    }
}
