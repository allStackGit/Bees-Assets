using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignLevelDataValidationTests
    {
        private Type _levelDataType;
        private Type _levelOptionsType;

        [SetUp]
        public void SetUp()
        {
            _levelDataType = RuntimeAssembly.GetType("Assets.Scripts.Data.LevelData");
            _levelOptionsType = RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions");
        }

        [Test]
        public void CampaignMissionNameComparisonIgnoresCaseWhitespaceAndPunctuationOnly()
        {
            Assert.That(RuntimeAssembly.InvokeStatic(_levelDataType, "MissionNamesMatch",
                " Of Production! ", "Of Production"), Is.True);
            Assert.That(RuntimeAssembly.InvokeStatic(_levelDataType, "MissionNamesMatch",
                "Bluer Pastures", "Pushback"), Is.False);
        }

        [Test]
        public void CurrentCampaignIdentityReturnsPersistedLevel()
        {
            object levelData = CreateCampaignData(CreateLevel(2, "Pushback"));
            object level = RuntimeAssembly.Invoke(levelData, "GetLevel", 2);

            Assert.That(RuntimeAssembly.GetField(level, "Id"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetField(level, "Name"), Is.EqualTo("Pushback"));
        }

        [Test]
        public void StaleCampaignIdentityFailsBeforeWrongMissionCanLoad()
        {
            object levelData = CreateCampaignData(CreateLevel(2, "Bluer Pastures"));

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(levelData, "GetLevel", 2));

            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            StringAssert.Contains("out of date", exception.InnerException.Message);
            StringAssert.Contains("Pushback", exception.InnerException.Message);
        }

        [Test]
        public void MissingPersistedCampaignMissionFailsExplicitly()
        {
            object levelData = CreateCampaignData();

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(levelData, "GetLevel", 8));

            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            StringAssert.Contains("Beenoculars", exception.InnerException.Message);
            StringAssert.Contains("missing", exception.InnerException.Message.ToLowerInvariant());
        }

        [Test]
        public void MatchingPersistedDataCanLoadEvenIfCatalogAvailabilityFlagIsStale()
        {
            object levelData = CreateCampaignData(CreateLevel(9, "On the Offensive"));
            object level = RuntimeAssembly.Invoke(levelData, "GetLevel", 9);

            Assert.That(RuntimeAssembly.GetField(level, "Id"), Is.EqualTo(9));
            Assert.That(RuntimeAssembly.GetField(level, "Name"), Is.EqualTo("On the Offensive"));
        }

        [Test]
        public void MissingRuntimeOnlyCampaignMissionStillFailsExplicitly()
        {
            object levelData = CreateCampaignData();

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(levelData, "GetLevel", 9));

            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            StringAssert.Contains("On the Offensive", exception.InnerException.Message);
            StringAssert.Contains("missing persisted level data", exception.InnerException.Message);
        }

        private object CreateCampaignData(params object[] levels)
        {
            object levelData = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.LevelData");
            RuntimeAssembly.SetField(levelData, "_type", 1);

            IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_levelOptionsType));
            foreach (object level in levels)
            {
                list.Add(level);
            }
            RuntimeAssembly.SetField(levelData, "_levels", list);
            return levelData;
        }

        private object CreateLevel(int id, string name)
        {
            return Activator.CreateInstance(_levelOptionsType, new object[] { id, 1, name });
        }
    }
}
