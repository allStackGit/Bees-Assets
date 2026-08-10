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
    }
}
