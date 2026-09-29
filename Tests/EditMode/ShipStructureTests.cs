using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ShipStructureTests
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships");
        }

        [Test]
        public void ShipImplementationIsSplitByResponsibility()
        {
            string core = File.ReadAllText(Path.Combine(_folder, "Ship.cs"));
            StringAssert.Contains("public partial class Ship : Entity", core);

            AssertPartial("Ship.Lifecycle.cs", "public override void Create", "public virtual void Setup", "public virtual void ClearData");
            AssertPartial("Ship.Movement.cs", "public void MoveToPoint", "private void MergePathfindingPaths");
            AssertPartial("Ship.Combat.cs", "public static void LogAttackingDamage", "public virtual void Kill");
            AssertPartial("Ship.Geometry.cs", "public Collider2D GetObstacleInPath", "public static double GetAverageHealthPercent");
            AssertPartial("Ship.Visuals.cs", "public virtual void SetColor", "public void UpdateHealthBar");
            AssertPartial("Ship.Interaction.cs", "public void Clicked", "OnTriggerEnter2D");
        }

        [Test]
        public void WeaponRangesAreComputedBeforeHalfRange()
        {
            string lifecycle = File.ReadAllText(Path.Combine(_folder, "Ship.Lifecycle.cs"));
            int maxRangeReset = lifecycle.IndexOf("MaxRange = 0;");
            int maxRangeAggregation = lifecycle.IndexOf("if (weapon.Range > MaxRange) MaxRange = weapon.Range;", maxRangeReset);
            int halfRange = lifecycle.IndexOf("HalfMaxRange = MaxRange / 2;", maxRangeAggregation);
            Assert.That(maxRangeReset, Is.GreaterThanOrEqualTo(0));
            Assert.That(maxRangeAggregation, Is.GreaterThan(maxRangeReset));
            Assert.That(halfRange, Is.GreaterThan(maxRangeAggregation));
        }

        [Test]
        public void HealthNormalizationUsesFractionalDivisionAndEmptyGuard()
        {
            string perception = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "RlCombatPerception.cs"));
            string geometry = File.ReadAllText(Path.Combine(_folder, "Ship.Geometry.cs"));

            StringAssert.Contains("Mathf.Clamp01((float)ship.Health / ship.MaxHealth)", perception);
            StringAssert.Contains("sensor.AddObservation(GetHealthFraction(ship));", perception);
            StringAssert.Contains("ships == null || ships.Count == 0", geometry);
            StringAssert.Contains("(double)ship.Health / ship.OriginalHealth", geometry);
        }

        [Test]
        public void DualCannonRecordsBothProjectilesInRlShotTelemetry()
        {
            string source = File.ReadAllText(Path.Combine(_folder, "Weapons", "DualCannon.cs"));
            const string marker = "global::RlOneVsOneEpisodeCoordinator.RecordShotFired(Ship, this);";

            StringAssert.Contains("Ship.FleetShip.ShotsFired += 2;", source);
            int first = source.IndexOf(marker);
            int second = source.IndexOf(marker, first + marker.Length);
            int third = source.IndexOf(marker, second + marker.Length);

            Assert.That(first, Is.GreaterThanOrEqualTo(0));
            Assert.That(second, Is.GreaterThan(first));
            Assert.That(third, Is.EqualTo(-1));
        }

        private void AssertPartial(string filename, params string[] markers)
        {
            string source = File.ReadAllText(Path.Combine(_folder, filename));
            StringAssert.Contains("public partial class Ship", source);
            foreach (string marker in markers)
            {
                StringAssert.Contains(marker, source);
            }
        }
    }
}
