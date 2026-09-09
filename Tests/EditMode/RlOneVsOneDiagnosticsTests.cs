using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneDiagnosticsTests
    {
        [Test]
        public void EpisodeDiagnosticsReportCombatAndRunningTrainingMetrics()
        {
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");

            Assert.That(coordinator, Does.Contain("SummaryIntervalEpisodes = 10"));
            Assert.That(coordinator, Does.Contain("BeeShotsFired"));
            Assert.That(coordinator, Does.Contain("BeeShotsHit"));
            Assert.That(coordinator, Does.Contain("BeeDamageDealt"));
            Assert.That(coordinator, Does.Contain("HumanShotsFired"));
            Assert.That(coordinator, Does.Contain("HumanShotsHit"));
            Assert.That(coordinator, Does.Contain("HumanDamageDealt"));
            Assert.That(coordinator, Does.Contain("bee_shots="));
            Assert.That(coordinator, Does.Contain("bee_hits="));
            Assert.That(coordinator, Does.Contain("bee_damage="));
            Assert.That(coordinator, Does.Contain("human_shots="));
            Assert.That(coordinator, Does.Contain("human_hits="));
            Assert.That(coordinator, Does.Contain("human_damage="));
            Assert.That(coordinator, Does.Contain("summary episodes="));
            Assert.That(coordinator, Does.Contain("bee_record="));
            Assert.That(coordinator, Does.Contain("human_record="));
            Assert.That(coordinator, Does.Contain("timeouts="));
            Assert.That(coordinator, Does.Contain("avg_duration="));
            Assert.That(coordinator, Does.Contain("bee_hit_rate="));
            Assert.That(coordinator, Does.Contain("human_hit_rate="));
        }

        [Test]
        public void NormalCombatPathReportsActualEnemyHitDamageToRlDiagnostics()
        {
            string combat = ReadSource("Scripts", "Entities", "Ships", "Ship.Combat.cs");

            Assert.That(combat, Does.Contain("RlOneVsOneEpisodeDiagnostics.RecordAttributedDamage"));
            Assert.That(combat, Does.Contain("RlOneVsOneEpisodeCoordinator.RecordHit"));
            Assert.That(combat, Does.Contain("math.min(power, target.Health)"));
            Assert.That(combat, Does.Contain("string rlDamageSource = \"gun\""));
            Assert.That(combat, Does.Contain("Ship rlDamageOwner = null"));
        }

        [Test]
        public void CompactBehaviorDiagnosticsCaptureCompositionDamageSourcesAndSpecialActions()
        {
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            string diagnostics = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeDiagnostics.cs");
            string registry = ReadSource("Scripts", "Levels", "GameState.Registry.cs");
            string combat = ReadSource("Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string fireBarge = ReadSource("Scripts", "Entities", "Ships", "FireBarge.cs");
            string striker = ReadSource("Scripts", "Entities", "Ships", "Striker.cs");
            string strikerBomb = ReadSource("Scripts", "Entities", "Projectiles", "StrikerBomb.cs");
            string rocketExplosion = ReadSource("Scripts", "Entities", "Projectiles", "RocketExplosion.cs");
            string yellowJacket = ReadSource("Scripts", "Entities", "Ships", "YellowJacket.cs");
            string barge = ReadSource("Scripts", "Entities", "Ships", "Barge.cs");
            string scout = ReadSource("Scripts", "Entities", "Ships", "Scout.cs");

            Assert.That(coordinator, Does.Contain("RlOneVsOneEpisodeDiagnostics.Begin(level)"));
            Assert.That(coordinator, Does.Contain("RlOneVsOneEpisodeDiagnostics.BuildEpisodeFields(timedOut)"));
            Assert.That(registry, Does.Contain("RlOneVsOneEpisodeDiagnostics.TrackShip(ship)"),
                "Newly added ships should register once instead of requiring another whole-fleet diagnostics pass.");
            Assert.That(diagnostics, Does.Not.Contain("private static void TrackSide("),
                "Diagnostics must not duplicate the coordinator's per-frame fleet traversal.");
            Assert.That(diagnostics, Does.Not.Contain("TrackSide(level.State.GetShips"),
                "Diagnostics must not perform a second whole-fleet scan.");

            Assert.That(diagnostics, Does.Contain("bee_ships="));
            Assert.That(diagnostics, Does.Contain("human_ships="));
            Assert.That(diagnostics, Does.Contain("bee_children="));
            Assert.That(diagnostics, Does.Contain("human_children="));
            Assert.That(diagnostics, Does.Contain("spawn{summary.Spawned},alive{summary.Alive},dmg{summary.Damage}"));
            Assert.That(diagnostics, Does.Contain("bee_striker_reloads="));
            Assert.That(diagnostics, Does.Contain("human_striker_reloads="));
            Assert.That(diagnostics, Does.Contain("bee_damage_sources="));
            Assert.That(diagnostics, Does.Contain("human_damage_sources="));
            Assert.That(diagnostics, Does.Contain("bee_damage_by_ship="));
            Assert.That(diagnostics, Does.Contain("human_damage_by_ship="));
            Assert.That(diagnostics, Does.Contain("bee_self_damage="));
            Assert.That(diagnostics, Does.Contain("human_self_damage="));
            Assert.That(diagnostics, Does.Contain("bee_friendly_damage="));
            Assert.That(diagnostics, Does.Contain("human_friendly_damage="));
            Assert.That(diagnostics, Does.Contain("bee_unattributed_damage="));
            Assert.That(diagnostics, Does.Contain("human_unattributed_damage="));
            Assert.That(diagnostics, Does.Contain("bee_specials="));
            Assert.That(diagnostics, Does.Contain("human_specials="));
            Assert.That(diagnostics, Does.Contain("bee_root_outcomes="));
            Assert.That(diagnostics, Does.Contain("human_root_outcomes="));
            Assert.That(diagnostics, Does.Contain("ChildShipTypes"));
            Assert.That(diagnostics, Does.Contain("ChildDamageByShipType"));
            Assert.That(diagnostics, Does.Contain("FormatChildren"));

            Assert.That(combat, Does.Contain("RlOneVsOneEpisodeDiagnostics.RecordShipDeath"));
            Assert.That(fireBarge, Does.Contain("RecordSpecialAction(this, \"fire_barge_detonate\")"));
            Assert.That(fireBarge, Does.Contain("LogDamage(Health, \"FireBarge\", true)"));
            Assert.That(fireBarge, Does.Contain("RlOneVsOneEpisodeDiagnostics.RecordShipDeath"));
            Assert.That(striker, Does.Contain("RecordSpecialAction(this, \"striker_bomb_drop\")"));
            Assert.That(striker, Does.Contain("RlOneVsOneEpisodeDiagnostics.RecordStrikerReplenished"));
            Assert.That(striker, Does.Contain("rlDamageSource: \"bomb\""));
            Assert.That(strikerBomb, Does.Contain("rlDamageSource: \"bomb\""));
            Assert.That(rocketExplosion, Does.Contain("FireBargeExplosion ? \"explosion\" : \"gun\""));
            Assert.That(yellowJacket, Does.Contain("RecordSpecialAction(this, \"yellow_jacket_detonate\")"));
            Assert.That(yellowJacket, Does.Contain("RecordAttributedDamage(diagnosticOwner, target, appliedDamage, \"bomb\")"));
            Assert.That(barge, Does.Contain("RecordSpecialAction(this, \"barge_charge\")"));
            Assert.That(barge, Does.Contain("rlDamageSource: \"charge\""));
            Assert.That(barge, Does.Contain("rlDamageOwner: this"));
            Assert.That(barge, Does.Contain("LogDamage(200, \"Barge\", true)"));
            Assert.That(scout, Does.Contain("RecordSpecialAction(this, \"scout_beacon\")"));
        }

        [Test]
        public void BehaviorDiagnosticsRemainOneEpisodeSummaryRatherThanEventLogSpam()
        {
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");
            string diagnostics = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeDiagnostics.cs");

            Assert.That(diagnostics, Does.Not.Contain("Debug.Log("),
                "Combat events should update counters only; diagnostics must not emit per-event log lines.");
            Assert.That(diagnostics, Does.Not.Contain("Carrier#"),
                "Episode output should aggregate Carrier information instead of listing runtime entity IDs.");
            Assert.That(diagnostics, Does.Not.Contain("Striker#"),
                "Episode output should aggregate Striker reloads instead of listing every child runtime ID.");
            Assert.That(CountOccurrences(coordinator, "behaviorDiagnostics"), Is.GreaterThanOrEqualTo(2),
                "The coordinator should build and append compact behavior diagnostics to the existing episode line.");
        }

        [Test]
        public void GunshipDualCannonReportsBothLaunchedProjectilesToRlDiagnostics()
        {
            string gunshipPrefab = ReadSource("Prefabs", "Entities", "Ships", "Gunship.prefab");
            string lifecycle = ReadSource("Scripts", "Entities", "Ships", "Ship.Lifecycle.cs");
            string dualCannon = ReadSource("Scripts", "Entities", "Ships", "Weapons", "DualCannon.cs");
            string turretAiming = ReadSource("Scripts", "Entities", "Ships", "Weapons", "Turret.Aiming.cs");

            Assert.That(gunshipPrefab, Does.Contain("value: Dual Cannon"),
                "The Gunship prefab must continue to use the Dual Cannon weapon presentation.");
            Assert.That(lifecycle, Does.Contain("ConfigData.WeaponTypes.DualCannon => gameObject.AddComponent<DualCannon>()"),
                "The authored Dual Cannon type must instantiate the DualCannon firing implementation.");
            Assert.That(CountOccurrences(dualCannon, "Level.AddProjectile(ConfigData.ProjectileTypes.HumanSmall"), Is.EqualTo(2),
                "A Dual Cannon volley must continue to launch two projectiles.");
            Assert.That(CountOccurrences(dualCannon, "RlOneVsOneEpisodeCoordinator.RecordShotFired(Ship, this);"), Is.EqualTo(2),
                "RL diagnostics must count both projectiles in a Dual Cannon volley.");
            Assert.That(CountOccurrences(turretAiming, "RlOneVsOneEpisodeCoordinator.RecordShotFired(Ship, this);"), Is.EqualTo(1),
                "A normal turret launch must continue to count exactly one projectile.");
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
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
