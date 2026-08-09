using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesCampaignScene")]
    public class CampaignScenarioAllMissionsSceneTests
    {
        private CampaignScenarioSceneHost _host;
        private Type _configDataType;
        private Type _isolationType;
        private Type _audioType;

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _isolationType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignScenarioIsolation");
            _audioType = RuntimeAssembly.GetType("Assets.Scripts.UI_Components.UIAudioController");
            AssertCleanStaticBoundary("before test");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_host != null)
            {
                yield return _host.Unload();
                _host = null;
            }
            AssertCleanStaticBoundary("after test");
        }

        [UnityTest]
        public IEnumerator EveryScriptedMissionCanOwnAndReleaseTheRealSpaceSceneInSequence()
        {
            string[] expectedMapNames = { "Pluto", "Neptune", "Titania", "Uranus" };

            for (int missionId = 0; missionId <= 11; missionId++)
            {
                _host = new CampaignScenarioSceneHost(missionId);
                yield return _host.Load();

                Assert.That(_host.LoadedScene.name, Is.EqualTo("Space"), $"mission {missionId}");
                Assert.That(_host.LoadedScene.isLoaded, Is.True, $"mission {missionId}");
                Assert.That(_host.Stage, Is.Not.Null, $"mission {missionId}");

                object prefabs = RuntimeAssembly.GetField(_host.Stage, "Prefabs");
                Assert.That(prefabs, Is.Not.Null, $"mission {missionId}");
                Assert.That(RuntimeAssembly.GetField(_host.Stage, "Pool"), Is.Not.Null, $"mission {missionId}");

                // Use the same production normalization that Stage runs before Pool.Setup.
                RuntimeAssembly.Invoke(prefabs, "LoadConversions");
                IList mapPrefabs = (IList)RuntimeAssembly.GetField(prefabs, "Maps");
                Assert.That(mapPrefabs.Count, Is.GreaterThanOrEqualTo(4), $"mission {missionId}");
                for (int mapIndex = 0; mapIndex < expectedMapNames.Length; mapIndex++)
                {
                    GameObject mapPrefab = (GameObject)mapPrefabs[mapIndex];
                    Assert.That(mapPrefab.name, Is.EqualTo(expectedMapNames[mapIndex]),
                        $"MapIndex {mapIndex} drifted while loading mission {missionId}.");
                }

                Assert.That(RuntimeAssembly.GetField(
                    RuntimeAssembly.GetField(_host.Level, "CurrentLevelOptions"), "Id"),
                    Is.EqualTo(missionId));
                Assert.That((int)_host.Driver.GetType().GetProperty("MissionId").GetValue(_host.Driver),
                    Is.EqualTo(missionId));
                Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.True);
                Assert.That((int)_isolationType.GetProperty("MissionId").GetValue(null), Is.EqualTo(missionId));
                Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "_socket"), Is.Null,
                    $"mission {missionId} created a socket during isolated bootstrap");
                Assert.That(_audioType.GetProperty("Instance").GetValue(null), Is.Null,
                    $"mission {missionId} created persistent audio during isolated bootstrap");

                yield return _host.Unload();
                _host = null;
                AssertCleanStaticBoundary($"after mission {missionId}");
                yield return null;
            }
        }

        private void AssertCleanStaticBoundary(string phase)
        {
            Assert.That(SceneManager.GetSceneByName("Space").isLoaded, Is.False,
                $"Space scene leaked {phase}.");
            Assert.That((bool)_isolationType.GetProperty("IsActive").GetValue(null), Is.False,
                $"Campaign isolation leaked {phase}.");
            Assert.That(RuntimeAssembly.GetStaticField(_configDataType, "_socket"), Is.Null,
                $"Global socket leaked {phase}.");
            Assert.That(_audioType.GetProperty("Instance").GetValue(null), Is.Null,
                $"Persistent audio singleton leaked {phase}.");
        }
    }
}
