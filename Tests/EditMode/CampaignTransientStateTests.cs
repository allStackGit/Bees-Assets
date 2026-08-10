using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesCampaignScenario")]
    public class CampaignTransientStateTests
    {
        private GameObject _levelObject;
        private object _level;
        private System.Type _levelType;

        [SetUp]
        public void SetUp()
        {
            _levelObject = new GameObject(nameof(CampaignTransientStateTests));
            _levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            _level = _levelObject.AddComponent(_levelType);
        }

        [TearDown]
        public void TearDown()
        {
            if (_levelObject != null)
            {
                Object.DestroyImmediate(_levelObject);
            }
        }

        [TestCase("_lastShipRetreated")]
        [TestCase("_hasSeenCarrierIntroIfNeeded")]
        public void MissionScopedFlagDoesNotLeakIntoNextLevelOptionsInstance(string propertyName)
        {
            object firstMission = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.LevelOptions");
            object secondMission = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.LevelOptions");
            PropertyInfo property = _levelType.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(property, Is.Not.Null, $"Missing mission-scoped property {propertyName}.");

            RuntimeAssembly.SetField(_level, "CurrentLevelOptions", firstMission);
            property.SetValue(_level, true);
            Assert.That(property.GetValue(_level), Is.True);

            RuntimeAssembly.SetField(_level, "CurrentLevelOptions", secondMission);
            Assert.That(property.GetValue(_level), Is.False,
                $"{propertyName} leaked from one mission/retry instance into another.");

            property.SetValue(_level, true);
            Assert.That(property.GetValue(_level), Is.True);

            RuntimeAssembly.SetField(_level, "CurrentLevelOptions", firstMission);
            Assert.That(property.GetValue(_level), Is.False,
                $"{propertyName} should be owned by exactly one LevelOptions instance.");
        }
    }
}
