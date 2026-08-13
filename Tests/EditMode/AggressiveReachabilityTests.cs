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
        public void AggressiveDoesNotSupersedeLivePathsOrBuildConnectivityOnClickFrame()
        {
            string aggressive = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Commands", "Aggressive.cs"));
            string trackedMovement = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.TrackedMovement.cs"));
            string interaction = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Interaction.cs"));

            Assert.That(aggressive, Does.Contain("ship.MoveToTrackedPoint(target.GetPosition());"));
            Assert.That(aggressive, Does.Not.Contain("ship.MoveToPoint(target.GetPosition());"));
            Assert.That(trackedMovement, Does.Contain("if (IsPathfinding)"));
            Assert.That(trackedMovement, Does.Contain("if (_tryingToFindPathAgain)"));
            Assert.That(trackedMovement, Does.Contain("if (IsFollowingPath)"));
            Assert.That(aggressive, Does.Match(@"if\s*\(IsHiveMindCommand\)\s*\{\s*PrepareDamageToSendEntries\(\);"));

            Assert.That(interaction, Does.Not.Contain("AreStaticallyConnected("));
            Assert.That(interaction, Does.Contain("UserAggressive(Squad)"));
        }
    }
}
