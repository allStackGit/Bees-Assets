using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RangeColliderVisibilityTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private int _nextMapObjectId = 1;

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in _objects)
            {
                if (gameObject != null)
                {
                    Object.DestroyImmediate(gameObject);
                }
            }
            _objects.Clear();
            _nextMapObjectId = 1;
        }

        [Test]
        public void ExitingMapObjectRemovesTheExitedObjectNotTheLastEnteredObject()
        {
            GameObject levelObject = CreateObject("Visibility Level");
            object level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            object state = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(level, "State", state);
            RuntimeAssembly.SetField(state, "Level", level);

            GameObject shipObject = CreateObject("Visibility Ship");
            object ship = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            RuntimeAssembly.SetField(ship, "Level", level);

            GameObject weaponObject = CreateObject("Visibility Weapon");
            object weapon = weaponObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.Weapon"));
            RuntimeAssembly.SetField(weapon, "Ship", ship);

            GameObject rangeObject = CreateObject("Visibility Range");
            object rangeCollider = rangeObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.RangeCollider"));
            RuntimeAssembly.SetField(rangeCollider, "Weapon", weapon);

            ObjectFixture first = CreateMapObject("First visible object");
            ObjectFixture second = CreateMapObject("Second visible object");

            RuntimeAssembly.Invoke(rangeCollider, "OnTriggerEnter2D", first.Collider);
            RuntimeAssembly.Invoke(rangeCollider, "OnTriggerEnter2D", second.Collider);

            object visibleObjects = RuntimeAssembly.GetField(state, "PlayerVisibleMapObjects");
            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(2));

            RuntimeAssembly.Invoke(rangeCollider, "OnTriggerExit2D", first.Collider);

            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(1),
                "Exactly the exiting map object should be removed from visibility.");
            Assert.That(CollectionContains(visibleObjects, second.MapObject), Is.True,
                "The object that remains in range must stay visible.");
            Assert.That(CollectionContains(visibleObjects, first.MapObject), Is.False,
                "The exited object must not remain in PlayerVisibleMapObjects.");
        }

        private ObjectFixture CreateMapObject(string name)
        {
            GameObject gameObject = CreateObject(name);
            gameObject.tag = "Object";
            Collider2D collider = gameObject.AddComponent<BoxCollider2D>();
            object mapObject = gameObject.AddComponent(RuntimeAssembly.GetType("MapObject"));
            RuntimeAssembly.SetField(mapObject, "Id", _nextMapObjectId++);
            return new ObjectFixture(mapObject, collider);
        }

        private GameObject CreateObject(string name)
        {
            GameObject gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }

        private static bool CollectionContains(object collection, object value)
        {
            foreach (object item in (System.Collections.IEnumerable)collection)
            {
                if (ReferenceEquals(item, value))
                {
                    return true;
                }
            }
            return false;
        }

        private sealed class ObjectFixture
        {
            public readonly object MapObject;
            public readonly Collider2D Collider;

            public ObjectFixture(object mapObject, Collider2D collider)
            {
                MapObject = mapObject;
                Collider = collider;
            }
        }
    }
}
