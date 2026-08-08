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
            LevelFixture level = CreateLevel();
            object rangeCollider = CreateRangeCollider(level.Level, "Primary range");
            ObjectFixture first = CreateMapObject("First visible object", level.Level);
            ObjectFixture second = CreateMapObject("Second visible object", level.Level);

            RuntimeAssembly.Invoke(rangeCollider, "OnTriggerEnter2D", first.Collider);
            RuntimeAssembly.Invoke(rangeCollider, "OnTriggerEnter2D", second.Collider);

            object visibleObjects = RuntimeAssembly.GetField(level.State, "PlayerVisibleMapObjects");
            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(2));

            RuntimeAssembly.Invoke(rangeCollider, "OnTriggerExit2D", first.Collider);

            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(1),
                "Exactly the exiting map object should be removed from visibility.");
            Assert.That(CollectionContains(visibleObjects, second.MapObject), Is.True,
                "The object that remains in range must stay visible.");
            Assert.That(CollectionContains(visibleObjects, first.MapObject), Is.False,
                "The exited object must not remain in PlayerVisibleMapObjects.");
        }

        [Test]
        public void MapObjectRemainsVisibleUntilEveryObservingWeaponRangeHasExited()
        {
            LevelFixture level = CreateLevel();
            object firstRange = CreateRangeCollider(level.Level, "First observing range");
            object secondRange = CreateRangeCollider(level.Level, "Second observing range");
            ObjectFixture target = CreateMapObject("Shared visible object", level.Level);
            object visibleObjects = RuntimeAssembly.GetField(level.State, "PlayerVisibleMapObjects");

            RuntimeAssembly.Invoke(firstRange, "OnTriggerEnter2D", target.Collider);
            RuntimeAssembly.Invoke(secondRange, "OnTriggerEnter2D", target.Collider);
            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(1));

            RuntimeAssembly.Invoke(firstRange, "OnTriggerExit2D", target.Collider);
            Assert.That(CollectionContains(visibleObjects, target.MapObject), Is.True,
                "One weapon leaving range must not hide an object still observed by another weapon.");

            RuntimeAssembly.Invoke(secondRange, "OnTriggerExit2D", target.Collider);
            Assert.That(CollectionContains(visibleObjects, target.MapObject), Is.False,
                "The object should be removed after its final observing range exits.");
        }

        private LevelFixture CreateLevel()
        {
            GameObject levelObject = CreateObject("Visibility Level");
            object level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            object state = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(level, "State", state);
            RuntimeAssembly.SetField(state, "Level", level);
            return new LevelFixture(level, state);
        }

        private object CreateRangeCollider(object level, string name)
        {
            GameObject shipObject = CreateObject(name + " Ship");
            object ship = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            RuntimeAssembly.SetField(ship, "Level", level);

            GameObject weaponObject = CreateObject(name + " Weapon");
            object weapon = weaponObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.Weapon"));
            RuntimeAssembly.SetField(weapon, "Ship", ship);

            GameObject rangeObject = CreateObject(name);
            object rangeCollider = rangeObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.RangeCollider"));
            RuntimeAssembly.SetField(rangeCollider, "Weapon", weapon);
            return rangeCollider;
        }

        private ObjectFixture CreateMapObject(string name, object level)
        {
            GameObject gameObject = CreateObject(name);
            gameObject.tag = "Object";
            Collider2D collider = gameObject.AddComponent<BoxCollider2D>();
            object mapObject = gameObject.AddComponent(RuntimeAssembly.GetType("MapObject"));
            RuntimeAssembly.SetField(mapObject, "Id", _nextMapObjectId++);
            RuntimeAssembly.SetField(mapObject, "Level", level);
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

        private sealed class LevelFixture
        {
            public readonly object Level;
            public readonly object State;

            public LevelFixture(object level, object state)
            {
                Level = level;
                State = state;
            }
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
