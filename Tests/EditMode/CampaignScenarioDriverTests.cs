using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesCampaignScenario")]
    public class CampaignScenarioDriverTests
    {
        private GameObject _levelObject;
        private object _level;
        private object _state;
        private Type _driverType;
        private Type _triggerType;

        [SetUp]
        public void SetUp()
        {
            _levelObject = new GameObject(nameof(CampaignScenarioDriverTests));
            _level = _levelObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            ((Behaviour)_level).enabled = false;
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            RuntimeAssembly.SetField(_level, "CurrentLevelOptions", Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions"),
                new object[] { 2, 1, "Pushback scenario" }));
            _driverType = RuntimeAssembly.GetType(
                "Assets.Scripts.Levels.CampaignScenarioDriver");
            _triggerType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Trigger");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_levelObject);
        }

        [Test]
        public void DriverAdvancesRealTriggerGraphWithoutWallClockAndDefersNestedTriggers()
        {
            int openingActionCount = 0;
            int terminalActionCount = 0;
            object terminal = CreateTrigger(
                () => true,
                () =>
                {
                    terminalActionCount++;
                    RuntimeAssembly.SetField(_level, "WinningSide", 1);
                    RuntimeAssembly.SetField(_state, "GameOver", true);
                },
                "Terminal outcome");
            object opening = CreateTrigger(
                () => true,
                () =>
                {
                    openingActionCount++;
                    RuntimeAssembly.AddToCollection(
                        RuntimeAssembly.GetField(_level, "NextTriggers"), terminal);
                },
                "Opening objective");
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(_level, "Triggers"), opening);

            object driver = Activator.CreateInstance(_driverType, new[] { _level, (object)2 });
            object first = RuntimeAssembly.Invoke(driver, "Advance");
            Assert.That(RuntimeAssembly.GetField(first, "TriggeredCount"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(first, "ActiveTriggerCount"), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(first, "DeferredTriggerCount"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(first, "GameOver"), Is.False);

            object second = RuntimeAssembly.Invoke(driver, "Advance");
            Assert.That(RuntimeAssembly.GetField(second, "TriggeredCount"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(second, "WinningSide"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(second, "GameOver"), Is.True);
            Assert.That(openingActionCount, Is.EqualTo(1));
            Assert.That(terminalActionCount, Is.EqualTo(1));

            object third = RuntimeAssembly.Invoke(driver, "Advance");
            Assert.That(RuntimeAssembly.GetField(third, "TriggeredCount"), Is.Zero);
            Assert.That(openingActionCount, Is.EqualTo(1));
            Assert.That(terminalActionCount, Is.EqualTo(1));
        }

        [TestCase(7)]
        [TestCase(8)]
        public void InDevelopmentTitaniaMissionsCanBeDrivenWithoutBeingConfigured(int missionId)
        {
            RuntimeAssembly.SetField(_level, "CurrentLevelOptions", Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions"),
                new object[] { missionId, 1, "In-development mission" }));

            object driver = Activator.CreateInstance(_driverType, new[] { _level, (object)missionId });
            Assert.That(driver, Is.Not.Null);
            PropertyInfo missionIdProperty = driver.GetType().GetProperty("MissionId");
            Assert.That(missionIdProperty, Is.Not.Null);
            Assert.That((int)missionIdProperty.GetValue(driver), Is.EqualTo(missionId));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_level, "Triggers")), Is.Zero,
                "Constructing a scenario driver must not configure or execute an in-development mission.");
        }

        [Test]
        public void DriverRejectsLevelOptionsFromAnotherMission()
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                Activator.CreateInstance(_driverType, new[] { _level, (object)1 }));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        private object CreateTrigger(Func<bool> condition, Action action, string name)
        {
            return Activator.CreateInstance(_triggerType, new object[] { condition, action, name });
        }
    }
}
