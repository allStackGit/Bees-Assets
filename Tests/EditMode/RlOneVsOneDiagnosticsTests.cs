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

            Assert.That(combat, Does.Contain("RlOneVsOneEpisodeCoordinator.RecordHit"));
            Assert.That(combat, Does.Contain("math.min(power, target.Health)"));
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
