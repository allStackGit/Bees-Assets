using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class AggressiveReachabilityTests
    {
        [Test]
        public void AggressiveRejectsPermanentlyDisconnectedTargetsBeforePathfinding()
        {
            string aggressive = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Commands", "Aggressive.cs"));
            string connectivity = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Pathfinder.Connectivity.cs"));

            Assert.That(aggressive, Does.Contain("AreStaticallyConnected(GetSquad().GetPosition(), EnemySquad.GetPosition(), connectivityClearance)"));
            Assert.That(aggressive, Does.Contain("SetFinalize(\"Enemy squad is in an unreachable map region\")"));
            Assert.That(aggressive, Does.Contain("if (IsHiveMindCommand)\n                {\n                    PrepareDamageToSendEntries();"),
                "User attacks must not do Hive Mind damage bookkeeping synchronously on the click frame.");
            Assert.That(connectivity, Does.Contain("private int[] BuildStaticConnectivity(int clearance)"));
            Assert.That(connectivity, Does.Contain("startComponent == components[destinationIndex]"));
        }
    }
}
