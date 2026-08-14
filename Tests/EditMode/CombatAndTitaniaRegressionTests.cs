using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CombatAndTitaniaRegressionTests
    {
        [Test]
        public void SelectedSquadBoundsRefreshAfterDeadShipIsRemoved()
        {
            string source = Read("Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string kill = ExtractMethodBody(source, "public virtual void Kill(");

            int removeShip = kill.IndexOf("Squad.RemoveShip(this);", StringComparison.Ordinal);
            int setOffsets = kill.IndexOf("Squad.SetOffsets();", StringComparison.Ordinal);
            int moveBox = kill.IndexOf("Squad.MoveSquadBox();", StringComparison.Ordinal);

            Assert.That(removeShip, Is.GreaterThanOrEqualTo(0));
            Assert.That(setOffsets, Is.GreaterThan(removeShip));
            Assert.That(moveBox, Is.GreaterThan(setOffsets),
                "Selection bounds must be recomputed from the surviving formation, not before the casualty is removed.");
        }

        [Test]
        public void HumanTargetBaseDeathDoesNotChangePlayerScoreOrShipLossCount()
        {
            string source = Read("Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string killedStats = ExtractMethodBody(source, "protected void LogKilledStats()");

            StringAssert.Contains("ShipType != ConfigData.ShipTypes.HumanTarget", killedStats);
            int exclusion = killedStats.IndexOf("ShipType != ConfigData.ShipTypes.HumanTarget", StringComparison.Ordinal);
            int score = killedStats.IndexOf("Level.State.PlayerScore -= FleetShip.GetTsv();", StringComparison.Ordinal);
            int shipLoss = killedStats.IndexOf("Level.State.PlayerShipsLost++;", StringComparison.Ordinal);

            Assert.That(score, Is.GreaterThan(exclusion));
            Assert.That(shipLoss, Is.GreaterThan(exclusion));
        }

        [Test]
        public void WeaponTargetsAndFiringRequireClearObstacleLineOfSight()
        {
            string weapon = Read("Scripts", "Entities", "Ships", "Weapons", "Weapon.cs");
            string turret = Read("Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");

            StringAssert.Contains("HasClearLineOfFire(TargetShip)", weapon);
            StringAssert.Contains("HasClearLineOfFire(potentialTargetShip)", weapon);
            StringAssert.Contains(
                "Physics2D.Linecast(origin, targetPoint, ConfigData.ObstaclesLayerMask).collider == null",
                weapon);
            StringAssert.Contains("PieceTransform.position", weapon);
            StringAssert.Contains("potentialTargetShip.Collider.ClosestPoint(origin)", weapon);
            StringAssert.Contains("HasClearLineOfFire(potentialTargetShip)", turret);
            StringAssert.DoesNotContain("Utilities.HasObstaclesInTheWay", turret);
        }

        [Test]
        public void TitaniaTwoEnemyCountIsReducedByAboutThirtyPercent()
        {
            string primary = Read("Scripts", "Levels", "Titania2Beenoculars.cs");
            string mirrored = Read("Scripts", "Levels", "Level.Titania2Enhancements.cs");

            MatchCollection primaryCounts = Regex.Matches(
                primary,
                @"GetSquadByComposition\(this, ConfigData\.ShipTypes\.[A-Za-z]+, (\d+), true, true\)");
            MatchCollection mirrorCounts = Regex.Matches(
                mirrored,
                @"\(ConfigData\.ShipTypes\.[A-Za-z]+, (\d+)\)");

            int primaryTotal = primaryCounts.Cast<Match>().Sum(match => int.Parse(match.Groups[1].Value));
            int mirrorTotal = mirrorCounts.Cast<Match>().Sum(match => int.Parse(match.Groups[1].Value));

            Assert.That(primaryTotal, Is.EqualTo(51));
            Assert.That(mirrorTotal, Is.EqualTo(51));
            Assert.That(primaryTotal + mirrorTotal, Is.EqualTo(102),
                "The previous full-duration nominal total was 146, so 102 is a 30.1% reduction.");
        }

        private static string Read(params string[] parts)
        {
            string path = Application.dataPath;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path);
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int method = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(method, Is.GreaterThanOrEqualTo(0), $"Could not find {signature}");
            int openingBrace = source.IndexOf('{', method);
            Assert.That(openingBrace, Is.GreaterThanOrEqualTo(0));

            int depth = 0;
            for (int i = openingBrace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(openingBrace, i - openingBrace + 1);
            }

            Assert.Fail($"Method {signature} has no balanced body.");
            return string.Empty;
        }
    }
}
