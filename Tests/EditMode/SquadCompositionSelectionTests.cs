using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadCompositionSelectionTests
    {
        [Test]
        public void FewerFallbackChoosesLargestAvailableSquadNotFirstPersistedMatch()
        {
            object selector = CreateSelector(new[]
            {
                CreateSquad(1, 1),
                CreateSquad(2, 5),
                CreateSquad(3, 7),
            });

            object result = InvokeCompositionLookup(selector, 8, true, true);

            Assert.That(RuntimeAssembly.GetField(result, "Id"), Is.EqualTo(3L));
        }

        [Test]
        public void MoreFallbackChoosesSmallestAvailableSquadWhenNoSmallerSquadExists()
        {
            object selector = CreateSelector(new[]
            {
                CreateSquad(4, 12),
                CreateSquad(5, 9),
            });

            object result = InvokeCompositionLookup(selector, 8, false, true);

            Assert.That(RuntimeAssembly.GetField(result, "Id"), Is.EqualTo(5L));
        }

        private static object InvokeCompositionLookup(object selector, int requestedCount, bool fewer, bool more)
        {
            object hornet = Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), "Hornet");
            MethodInfo method = RuntimeAssembly.GetType("Assets.Scripts.Ships").GetMethod(
                "GetSquadByComposition",
                new[]
                {
                    RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"),
                    hornet.GetType(),
                    typeof(int),
                    typeof(bool),
                    typeof(bool)
                });
            Assert.That(method, Is.Not.Null);
            return method.Invoke(selector, new[] { null, hornet, (object)requestedCount, fewer, more });
        }

        private static object CreateSelector(IEnumerable<object> squads)
        {
            Type shipsType = RuntimeAssembly.GetType("Assets.Scripts.Ships");
            object selector = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Ships");
            object savedData = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquadsData");
            Type savedSquadType = RuntimeAssembly.GetType("Assets.Scripts.Data.SavedSquad");
            Type listType = typeof(List<>).MakeGenericType(savedSquadType);
            IList list = (IList)Activator.CreateInstance(listType);
            foreach (object squad in squads)
            {
                list.Add(squad);
            }

            FieldInfo listField = savedData.GetType().GetField(
                "_savedSquadsList",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(listField, Is.Not.Null);
            listField.SetValue(savedData, list);

            FieldInfo savedDataField = shipsType.GetField(
                "_savedSquadsData",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(savedDataField, Is.Not.Null);
            savedDataField.SetValue(selector, savedData);
            return selector;
        }

        private static object CreateSquad(long id, int shipCount)
        {
            Type savedSquadType = RuntimeAssembly.GetType("Assets.Scripts.Data.SavedSquad");
            object hornet = Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), "Hornet");
            object shooting = Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShootingStrategyTypes"), "Closest");
            object squad = Activator.CreateInstance(savedSquadType, new object[]
            {
                id,
                2,
                "Hornets " + shipCount,
                Vector2.zero,
                false,
                false,
                shooting,
                new Color(-1f, -1f, -1f, -1f),
                null
            });

            Type fleetShipType = RuntimeAssembly.GetType("Assets.Scripts.Data.FleetShip");
            Type squadShipType = RuntimeAssembly.GetType("Assets.Scripts.Data.SquadShip");
            MethodInfo addShip = savedSquadType.GetMethod("AddShipToSquad");
            for (int index = 0; index < shipCount; index++)
            {
                object fleetShip = Activator.CreateInstance(fleetShipType, new object[]
                {
                    -(id * 100 + index + 1),
                    hornet,
                    false,
                    false,
                    0, 0, 0, 0, 0, 0, 0
                });
                object squadShip = Activator.CreateInstance(squadShipType, new[] { fleetShip, (object)Vector2.zero });
                addShip.Invoke(squad, new[] { squadShip });
            }
            return squad;
        }
    }
}
