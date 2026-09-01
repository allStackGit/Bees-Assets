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
                BindingFlags.Static | BindingFlags.NonPublic);
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
        public void DurabilityCurriculumRunsBeforeAgentActionsAndPreservesAuthoredStatsAndTsv()
        {
            DefaultExecutionOrder executionOrder = _guardType.GetCustomAttribute<DefaultExecutionOrder>();
            Assert.That(executionOrder, Is.Not.Null);
            Assert.That(executionOrder.order, Is.LessThan(0));

            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Scenes",
                "RlOneVsOneTrainingDurabilityGuard.cs"));

            Assert.That(source, Does.Contain("RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime"));
            Assert.That(source, Does.Contain("RlOneVsOneTrainingBootstrap.IsActiveFor(_stage)"));
            Assert.That(source, Does.Contain("ship.MaxHealth = trainingHealth;"));
            Assert.That(source, Does.Contain("if (ship.Health > trainingHealth)"));
            Assert.That(source, Does.Contain("ship.Health = trainingHealth;"));
            Assert.That(source, Does.Not.Contain("ship.OriginalHealth ="));
            Assert.That(source, Does.Not.Contain("ship.OriginalTsv ="));
            Assert.That(source, Does.Not.Contain("ship.Tsv ="));
        }

        private int CalculateTrainingHealth(int originalHealth)
        {
            return (int)_calculateTrainingHealth.Invoke(null, new object[] { originalHealth });
        }
    }
}
