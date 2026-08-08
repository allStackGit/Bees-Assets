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
                Assert.That(intro.clip.length, Is.EqualTo(introEnd).Within(2f / 44100f));
                Assert.That(loop.clip.length, Is.EqualTo(loopEnd - introEnd).Within(2f / 44100f));
                Assert.That(intro.transform.parent, Is.EqualTo(gameObject.transform));
                Assert.That(loop.transform.parent, Is.EqualTo(gameObject.transform));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
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
