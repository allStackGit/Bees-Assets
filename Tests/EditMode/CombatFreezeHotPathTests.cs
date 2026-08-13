using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CombatFreezeHotPathTests
    {
        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            foreach (string part in pathParts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        [Test]
        public void HiveMindFirstContactUsesMaintainedSideVisibilityCache()
        {
            string queries = ReadSource("Scripts", "Levels", "GameState.Queries.cs");
            string vision = ReadSource("Scripts", "Entities", "Ships", "Weapons", "HivemindVision.cs");

            Assert.That(queries, Does.Contain("public bool RecordHiveMindSighting(Ship observer, Ship spotted)"));
            Assert.That(queries, Does.Contain("return VisionCache[sideIndex].Add(spotted);"));
            Assert.That(queries, Does.Contain("return VisionCache[side - 1];"));
            Assert.That(queries, Does.Not.Contain("HivemindShips[side - 1].Aggregate"),
                "Pairwise sight triggers must not rebuild the entire faction visibility graph.");
            Assert.That(vision, Does.Contain("state.RecordHiveMindSighting(Ship, _shipEnter)"));
            Assert.That(vision, Does.Not.Contain("GetShipsVisibleToHiveMind(Ship.Side).Contains"));
        }

        [Test]
        public void WeaponRangeCacheContainsEnemyCandidatesOnly()
        {
            string range = ReadSource("Scripts", "Entities", "Ships", "Weapons", "RangeCollider.cs");
            string turret = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs");

            Assert.That(range, Does.Contain("_shipEnter.Side != Weapon.Ship.Side"));
            Assert.That(range, Does.Contain("!Weapon.ShipsWithinRange.ContainsKey(_shipEnter.Id)"));
            Assert.That(turret, Does.Contain("potentialTargetShip.Side != Side"),
                "Target validation must retain a second enemy-side guard even when the range cache is correct.");
        }

        [Test]
        public void SquadCenterDoesNotSortShipsToFindBounds()
        {
            string geometry = ReadSource("Scripts", "Levels", "Squad.Geometry.cs");

            Assert.That(geometry, Does.Contain("private bool TryGetBounds"));
            Assert.That(geometry, Does.Contain("for (int i = 1; i < ships.Count; i++)"));
            Assert.That(geometry, Does.Not.Contain("OrderBy("));
            Assert.That(geometry, Does.Not.Contain("OrderByDescending("));
        }

        [Test]
        public void FreezeDiagnosticsAreOptInAndAggregateOncePerSecond()
        {
            string diagnostics = ReadSource("Scripts", "Levels", "FreezeDiagnostics.cs");

            Assert.That(diagnostics, Does.Contain("public bool EnableFreezeDiagnostics;"));
            Assert.That(diagnostics, Does.Contain("now - counters.IntervalStart < 1f"));
            Assert.That(diagnostics, Does.Contain("[FreezeDiag:{level.Name}]"));
            Assert.That(diagnostics, Does.Contain("pathQueue="));
            Assert.That(diagnostics, Does.Contain("activePathWorkers="));
            Assert.That(diagnostics, Does.Contain("hiveSightEnters="));
            Assert.That(diagnostics, Does.Contain("weaponRangeEnters="));
            Assert.That(diagnostics, Does.Contain("turretCandidates="));
        }
    }
}
