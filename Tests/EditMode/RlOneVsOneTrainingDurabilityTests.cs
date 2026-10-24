using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneTrainingDurabilityTests
    {
        private TypeInfo _guardType;
        private MethodInfo _calculateTrainingHealth;

        [SetUp]
        public void SetUp()
        {
            _guardType = RuntimeAssembly.GetType("RlOneVsOneTrainingDurabilityGuard").GetTypeInfo();
            _calculateTrainingHealth = _guardType.GetMethod(
                "CalculateTrainingHealth",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(int) },
                null);
            Assert.That(_calculateTrainingHealth, Is.Not.Null);
        }

        [Test]
        public void FirstCurriculumStageUsesTwentyFivePercentDurability()
        {
            FieldInfo fraction = _guardType.GetField(
                "TrainingHealthFraction",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(fraction, Is.Not.Null);
            Assert.That(fraction.GetValue(null), Is.EqualTo(0.25f));

            Assert.That(CalculateTrainingHealth(300), Is.EqualTo(75));
            Assert.That(CalculateTrainingHealth(301), Is.EqualTo(76));
            Assert.That(CalculateTrainingHealth(1), Is.EqualTo(1));
            Assert.That(CalculateTrainingHealth(0), Is.EqualTo(0));
        }

        [Test]
        public void DurabilityCurriculumAppliesAtShipSetupAndPreservesAuthoredStatsAndTsv()
        {
            // The common Ship.Setup path covers primary and secondary arenas,
            // pooled episode resets and mid-episode spawns without frame polling.
            Assert.That(_guardType.IsAbstract && _guardType.IsSealed, Is.True);

            string shipSetup = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Lifecycle.cs"));
            Assert.That(shipSetup, Does.Contain("RlOneVsOneTrainingBootstrap.IsActiveFor(Stage)"));
            Assert.That(shipSetup, Does.Contain("RlOneVsOneTrainingDurabilityGuard.ApplyTrainingDurability(this)"));

            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "RlOneVsOneTrainingDurabilityGuard.cs"));
            Assert.That(source, Does.Not.Contain("FixedUpdate()"));
            Assert.That(source, Does.Contain("ship.MaxHealth = trainingHealth;"));
            Assert.That(source, Does.Contain("if (ship.Health > trainingHealth)"));
            Assert.That(source, Does.Contain("ship.Health = trainingHealth;"));
            Assert.That(source, Does.Not.Contain("ship.OriginalHealth ="));
            Assert.That(source, Does.Not.Contain("ship.OriginalTsv ="));
            Assert.That(source, Does.Not.Contain("ship.Tsv ="));
        }

        [Test]
        public void ReapplyingCurriculumRefreshesHealthBarAfterPooledReset()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Scenes",
                "RlOneVsOneTrainingDurabilityGuard.cs"));

            Assert.That(source, Does.Contain("bool durabilityChanged = ship.MaxHealth != trainingHealth || ship.Health > trainingHealth;"));
            Assert.That(source, Does.Contain("if (durabilityChanged)"));
            Assert.That(source, Does.Contain("ship.UpdateHealthBar();"));
        }

        private int CalculateTrainingHealth(int originalHealth)
        {
            return (int)_calculateTrainingHealth.Invoke(null, new object[] { originalHealth });
        }
    }
}
