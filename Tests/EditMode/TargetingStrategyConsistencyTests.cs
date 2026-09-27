using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TargetingStrategyConsistencyTests
    {
        private static string Read(params string[] path) => File.ReadAllText(Path.Combine(Application.dataPath, Path.Combine(path)));

        [Test]
        public void CommandLeastHealthOrdersByRemainingHealth()
        {
            string command = Read("Scripts", "Levels", "Commands", "Command.cs");

            Assert.That(command, Does.Contain("case ConfigData.ShootingStrategyTypes.LeastHealth:"));
            Assert.That(command, Does.Contain("_tempShips.Sort((a, b) => a.Health.CompareTo(b.Health));"));
            Assert.That(command, Does.Not.Contain("(a.Health - a.OriginalHealth).CompareTo(b.Health - b.OriginalHealth)"));
        }

        [Test]
        public void MatchupTypeXUsesNormalTypeTargetingBranch()
        {
            string matchup = Read("Scripts", "Levels", "Commands", "MatchupStrategy.cs");

            Assert.That(matchup, Does.Contain("case ConfigData.MatchupStrategyTypes.TypeX:"));
            Assert.That(matchup, Does.Contain("_type = Utilities.ConvertMatchupStrategyToShipType[MatchupType];"));
        }

        [Test]
        public void WeaponTargetDistanceUsesConsistentWorldSpaceCoordinates()
        {
            string weapon = Read("Scripts", "Entities", "Ships", "Weapons", "Weapon.cs");
            int method = weapon.IndexOf("public float DistanceTo(Entity entity)");
            int nextMethod = weapon.IndexOf("public float AngleToPoint", method);
            Assert.That(method, Is.GreaterThanOrEqualTo(0));
            Assert.That(nextMethod, Is.GreaterThan(method));
            string body = weapon.Substring(method, nextMethod - method);

            Assert.That(body, Does.Contain("Ship.Level.Map.Transform.TransformPoint(GetPosition())"));
            Assert.That(body, Does.Contain("entity.Collider.ClosestPoint(worldPosition)"));
            Assert.That(body, Does.Contain("Vector2.Distance(worldPosition, closestWorldPoint)"));
            Assert.That(body, Does.Not.Contain("ClosestPoint(GetPosition())"),
                "Collider2D.ClosestPoint must not receive a map-local weapon position.");
        }
    }
}
