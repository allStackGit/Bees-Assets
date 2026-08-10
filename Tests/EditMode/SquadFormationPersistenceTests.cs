using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadFormationPersistenceTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in _objects)
            {
                if (gameObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                }
            }
            _objects.Clear();
        }

        [Test]
        public void StartingPositionPreservesSavedOffsetsAcrossDifferentShipSizes()
        {
            GameObject squadObject = CreateObject("Formation squad");
            object squad = squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));

            object barge = CreateShip("Barge", "Barge", new Vector2(-30f, 12f));
            object honeybee = CreateShip("Honeybee", "Honeybee", new Vector2(17f, -9f));
            RuntimeAssembly.Invoke(squad, "AddShip", barge);
            RuntimeAssembly.Invoke(squad, "AddShip", honeybee);

            Vector2 center = new Vector2(100f, -50f);
            RuntimeAssembly.Invoke(squad, "SetStartingPosition", center);

            Assert.That(((Component)barge).transform.localPosition,
                Is.EqualTo((Vector3)(center + new Vector2(-30f, 12f))));
            Assert.That(((Component)honeybee).transform.localPosition,
                Is.EqualTo((Vector3)(center + new Vector2(17f, -9f))));
        }

        private object CreateShip(string objectName, string shipTypeName, Vector2 savedOffset)
        {
            GameObject shipObject = CreateObject(objectName);
            object ship = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            RuntimeAssembly.SetField(ship, "Transform", shipObject.transform);
            RuntimeAssembly.SetField(ship, "OffsetFromCenter", savedOffset);
            RuntimeAssembly.SetField(ship, "ShipType", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), shipTypeName));
            return ship;
        }

        private GameObject CreateObject(string name)
        {
            GameObject gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }
    }
}
