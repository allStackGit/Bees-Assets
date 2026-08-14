using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesCampaignScenario")]
    public class TitaniaLocationContractTests
    {
        [Test]
        public void ExistingLocationEnumValuesRemainStableWhileMapOrderChanges()
        {
            Type configDataType = FindRuntimeType("Assets.Scripts.ConfigData");
            Type locationsType = configDataType.GetNestedType("Locations", BindingFlags.Public);

            // Locations is a semantic serialized enum and is deliberately distinct
            // from MapIndex. Do not renumber it when campaign map order changes.
            Assert.That(Convert.ToInt32(Enum.Parse(locationsType, "Pluto")), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(Enum.Parse(locationsType, "Neptune")), Is.EqualTo(1));
            Assert.That(Convert.ToInt32(Enum.Parse(locationsType, "Uranus")), Is.EqualTo(2));
            Assert.That(Convert.ToInt32(Enum.Parse(locationsType, "Titania")), Is.EqualTo(3));
        }

        [Test]
        public void MapIndicesFollowCampaignOrderAndTitaniaUsesItsOwnStartingPositions()
        {
            Type configDataType = FindRuntimeType("Assets.Scripts.ConfigData");
            IList maps = (IList)configDataType.GetField("Maps", BindingFlags.Public | BindingFlags.Static).GetValue(null);

            Assert.That(maps.Count, Is.GreaterThanOrEqualTo(4));
            AssertMap(maps[0], 0, "Pluto", new Vector2(0, -230), new Vector2(0, 230));
            AssertMap(maps[1], 1, "Neptune", new Vector2(0, -430), new Vector2(0, 430));
            AssertMap(maps[2], 2, "Titania", new Vector2(0, -215), new Vector2(0, 215));
            AssertMap(maps[3], 3, "Uranus", new Vector2(0, -430), new Vector2(0, 430));
        }

        [Test]
        public void MissingTitaniaSceneReferenceIsRecoveredFromMapPrefabCatalog()
        {
            Type configDataType = FindRuntimeType("Assets.Scripts.ConfigData");
            Type prefabsType = FindRuntimeType("Assets.Scripts.Levels.Prefabs");
            IList configuredMaps = (IList)configDataType.GetField("Maps", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            FieldInfo mapsField = prefabsType.GetField("Maps", BindingFlags.Public | BindingFlags.Instance);

            GameObject catalogObject = Resources.Load<GameObject>("Map Prefab Catalog");
            Assert.That(catalogObject, Is.Not.Null, "The map prefab fallback catalog is not loadable from Resources.");

            Component catalog = catalogObject.GetComponent(prefabsType);
            Assert.That(catalog, Is.Not.Null, "The map prefab fallback catalog is missing its Prefabs component.");
            IList catalogMaps = (IList)mapsField.GetValue(catalog);
            Assert.That(catalogMaps.Count, Is.EqualTo(configuredMaps.Count));
            AssertMapPrefabOrder(catalogMaps, configuredMaps);

            // Reproduce the stale Hivemind Training scene state: Pluto, Neptune and Uranus,
            // with Titania omitted. NormalizeMapPrefabs must restore Titania at map index 2.
            GameObject host = new GameObject("Map Prefab Recovery Test");
            try
            {
                Component runtimePrefabs = host.AddComponent(prefabsType);
                IList incompleteMaps = (IList)Activator.CreateInstance(mapsField.FieldType);
                incompleteMaps.Add(catalogMaps[0]);
                incompleteMaps.Add(catalogMaps[1]);
                incompleteMaps.Add(catalogMaps[3]);
                mapsField.SetValue(runtimePrefabs, incompleteMaps);

                MethodInfo normalize = prefabsType.GetMethod("NormalizeMapPrefabs", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(normalize, Is.Not.Null);
                normalize.Invoke(runtimePrefabs, null);

                IList repairedMaps = (IList)mapsField.GetValue(runtimePrefabs);
                Assert.That(repairedMaps.Count, Is.EqualTo(configuredMaps.Count));
                AssertMapPrefabOrder(repairedMaps, configuredMaps);
                Assert.That(((GameObject)repairedMaps[2]).name, Is.EqualTo("Titania"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void TitaniaSourceBuildsAnalyzedIntroAndLoopAndWiresLoopingSource()
        {
            Type builderType = FindRuntimeType("Assets.Scripts.TitaniaMusicBuilder");
            Type audioControllerType = FindRuntimeType("Assets.Scripts.AudioController");

            string resourcePath = (string)builderType.GetField("ResourcePath",
                BindingFlags.Public | BindingFlags.Static).GetRawConstantValue();
            float introEnd = (float)builderType.GetField("IntroEndSeconds",
                BindingFlags.Public | BindingFlags.Static).GetRawConstantValue();
            float loopEnd = (float)builderType.GetField("LoopEndSeconds",
                BindingFlags.Public | BindingFlags.Static).GetRawConstantValue();

            Assert.That(resourcePath, Is.EqualTo("Music/Titania/Titania Source"));
            Assert.That(introEnd, Is.EqualTo(26.565215f).Within(0.00001f));
            Assert.That(loopEnd, Is.EqualTo(185.461179f).Within(0.00001f));

            AudioClip source = Resources.Load<AudioClip>(resourcePath);
            Assert.That(source, Is.Not.Null, "Titania source soundtrack is not loadable from Resources.");
            Assert.That(source.channels, Is.EqualTo(2));
            Assert.That(source.frequency, Is.EqualTo(44100));
            Assert.That(source.length, Is.GreaterThan(loopEnd));
            Resources.UnloadAsset(source);

            GameObject gameObject = new GameObject("Titania Audio Contract");
            try
            {
                Component controller = gameObject.AddComponent(audioControllerType);
                MethodInfo ensure = audioControllerType.GetMethod("EnsureTitaniaMusicSources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(ensure, Is.Not.Null);
                ensure.Invoke(controller, null);

                AudioSource intro = (AudioSource)audioControllerType.GetField("TitaniaIntro").GetValue(controller);
                AudioSource loop = (AudioSource)audioControllerType.GetField("TitaniaLoop").GetValue(controller);

                Assert.That(intro, Is.Not.Null);
                Assert.That(loop, Is.Not.Null);
                Assert.That(intro.clip, Is.Not.Null);
                Assert.That(loop.clip, Is.Not.Null);
                Assert.That(intro.loop, Is.False);
                Assert.That(loop.loop, Is.True);

                int expectedIntroSamples = Mathf.RoundToInt(introEnd * 44100f);
                int expectedLoopEndSamples = Mathf.RoundToInt(loopEnd * 44100f);
                Assert.That(intro.clip.samples, Is.EqualTo(expectedIntroSamples));
                Assert.That(loop.clip.samples, Is.EqualTo(expectedLoopEndSamples - expectedIntroSamples));

                // AudioClip.length is an approximate floating-point presentation of the
                // sample count, so keep the musical contract sample-exact and only use a
                // millisecond-level sanity check for the reported duration.
                Assert.That(intro.clip.length, Is.EqualTo(introEnd).Within(0.001f));
                Assert.That(loop.clip.length, Is.EqualTo(loopEnd - introEnd).Within(0.001f));
                Assert.That(intro.transform.parent, Is.EqualTo(gameObject.transform));
                Assert.That(loop.transform.parent, Is.EqualTo(gameObject.transform));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static void AssertMapPrefabOrder(IList mapPrefabs, IList configuredMaps)
        {
            Assert.That(mapPrefabs.Count, Is.EqualTo(configuredMaps.Count));
            for (int i = 0; i < configuredMaps.Count; i++)
            {
                object configuredMap = configuredMaps[i];
                string expectedName = (string)configuredMap.GetType().GetField("Name").GetValue(configuredMap);
                Assert.That(((GameObject)mapPrefabs[i]).name, Is.EqualTo(expectedName), $"Map prefab at index {i} does not match ConfigData.Maps.");
            }
        }

        private static void AssertMap(object map, int expectedId, string expectedLocation, Vector2 expectedUserStart, Vector2 expectedAiStart)
        {
            Type mapType = map.GetType();
            Assert.That((int)mapType.GetField("Id").GetValue(map), Is.EqualTo(expectedId));
            Assert.That(mapType.GetField("Location").GetValue(map).ToString(), Is.EqualTo(expectedLocation));
            Assert.That((Vector2)mapType.GetField("UserStartingPosition").GetValue(map), Is.EqualTo(expectedUserStart));
            Assert.That((Vector2)mapType.GetField("AIStartingPosition").GetValue(map), Is.EqualTo(expectedAiStart));
        }

        private static Type FindRuntimeType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            Assert.Fail($"Could not find runtime type {fullName}.");
            return null;
        }
    }
}