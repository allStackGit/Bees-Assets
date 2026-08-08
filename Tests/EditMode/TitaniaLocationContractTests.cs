using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    public class TitaniaLocationContractTests
    {
        private static Assembly RuntimeAssembly => typeof(UnityEngine.Object).Assembly.GetType("Assets.Scripts.ConfigData")?.Assembly
            ?? AppDomain.CurrentDomain.GetAssemblies()[0];

        [Test]
        public void ExistingLocationEnumValuesRemainStableAndTitaniaIsAppended()
        {
            Type configDataType = FindRuntimeType("Assets.Scripts.ConfigData");
            Type locationsType = configDataType.GetNestedType("Locations", BindingFlags.Public);

            Assert.That(Convert.ToInt32(Enum.Parse(locationsType, "Pluto")), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(Enum.Parse(locationsType, "Neptune")), Is.EqualTo(1));
            Assert.That(Convert.ToInt32(Enum.Parse(locationsType, "Uranus")), Is.EqualTo(2));
            Assert.That(Convert.ToInt32(Enum.Parse(locationsType, "Titania")), Is.EqualTo(3));
        }

        [Test]
        public void MapIndicesPreservePersistedOrderAndTitaniaUsesItsOwnStartingPositions()
        {
            Type configDataType = FindRuntimeType("Assets.Scripts.ConfigData");
            IList maps = (IList)configDataType.GetField("Maps", BindingFlags.Public | BindingFlags.Static).GetValue(null);

            Assert.That(maps.Count, Is.GreaterThanOrEqualTo(4));
            AssertMap(maps[0], 0, "Pluto", new Vector2(0, -230), new Vector2(0, 230));
            AssertMap(maps[1], 1, "Neptune", new Vector2(0, -430), new Vector2(0, 430));
            AssertMap(maps[2], 2, "Uranus", new Vector2(0, -430), new Vector2(0, 430));
            AssertMap(maps[3], 3, "Titania", new Vector2(0, -215), new Vector2(0, 215));
        }

        [Test]
        public void TitaniaAudioControllerHasAnalyzedIntroHandoffDefault()
        {
            Type audioControllerType = FindRuntimeType("Assets.Scripts.AudioController");
            GameObject gameObject = new GameObject("Titania Audio Contract");
            try
            {
                Component controller = gameObject.AddComponent(audioControllerType);
                float introLength = (float)audioControllerType.GetField("TitaniaIntroLength").GetValue(controller);
                Assert.That(introLength, Is.EqualTo(26.565216f).Within(0.00001f));
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
