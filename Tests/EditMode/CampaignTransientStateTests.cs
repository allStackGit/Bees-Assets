using System.IO;
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

        [Test]
        public void CampaignConfigurationClearsActiveAndDeferredTriggersBeforeNewMission()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("private void SetTriggers()");
            int end = source.IndexOf("public void EasterEggTriggers()", start);

            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));
            string method = source.Substring(start, end - start);

            StringAssert.Contains("Triggers.Clear();", method);
            StringAssert.Contains("NextTriggers.Clear();", method);
            StringAssert.Contains("HasContinuousTriggers = false;", method);
            StringAssert.Contains("CampaignMissionCatalog.Configure(this, CurrentLevelOptions.Id);", method);
        }
    }
}
