using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesCampaignScene")]
    public class CampaignScenarioSceneHostTests
    {
        private CampaignScenarioSceneHost _host;
        private Type _configDataType;

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "_socket"), Is.Null,
                "The isolated scene category must start without a global socket.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_host != null)
            {
                yield return _host.Unload();
                _host = null;
            }
        }

        [UnityTest]
        public IEnumerator ScriptedMissionLoadsRealSpaceSceneWithoutSocketOrPersistentBootstrap()
        {
            _host = new CampaignScenarioSceneHost(2);
            yield return _host.Load();

            Assert.That(_host.SceneLoadRequested, Is.True);
            Assert.That(_host.LoadedScene.name, Is.EqualTo("Space"));
            Assert.That(_host.LoadedScene.isLoaded, Is.True);
            Assert.That(_host.Stage, Is.Not.Null);
            Assert.That(((Behaviour)_host.Stage).enabled, Is.False);
            Assert.That(RuntimeAssembly.GetField(_host.Stage, "Prefabs"), Is.Not.Null,
                "Space must retain its serialized prefab registry for future full mission setup.");
            Assert.That(RuntimeAssembly.GetField(_host.Stage, "Pool"), Is.Not.Null,
                "Space must retain its serialized pool for future full mission setup.");
            Assert.That(RuntimeAssembly.GetField(
                RuntimeAssembly.GetField(_host.Level, "CurrentLevelOptions"), "Id"), Is.EqualTo(2));
            Assert.That((int)_host.Driver.GetType().GetProperty("MissionId").GetValue(_host.Driver), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "_socket"), Is.Null,
                "Loading an isolated campaign scene must not create the global socket.");

            Type isolation = RuntimeAssembly.GetType(
                "Assets.Scripts.Levels.CampaignScenarioIsolation");
            Assert.That((bool)isolation.GetProperty("IsActive").GetValue(null), Is.True);
            Assert.That((int)isolation.GetProperty("MissionId").GetValue(null), Is.EqualTo(2));
            Type audioController = RuntimeAssembly.GetType(
                "Assets.Scripts.UI_Components.UIAudioController");
            Assert.That(audioController.GetProperty("Instance").GetValue(null), Is.Null,
                "Isolated Space loading must not create a DontDestroyOnLoad audio singleton.");

            yield return _host.Unload();
            _host = null;
            Assert.That(SceneManager.GetSceneByName("Space").isLoaded, Is.False);
            Assert.That((bool)isolation.GetProperty("IsActive").GetValue(null), Is.False);
            Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "_socket"), Is.Null);
        }

        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        [TestCase(10)]
        [TestCase(11)]
        public void LateScriptedMissionCanCreateHostWithoutStartingSceneOrServices(int missionId)
        {
            Assert.That(SceneManager.GetSceneByName("Space").isLoaded, Is.False);
            _host = new CampaignScenarioSceneHost(missionId);
            Assert.That(_host.SceneLoadRequested, Is.False);
            Assert.That(SceneManager.GetSceneByName("Space").isLoaded, Is.False);
            Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "_socket"), Is.Null);
            _host = null;
        }
    }
}
