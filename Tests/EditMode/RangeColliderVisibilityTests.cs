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

        [Test]
        public void DeactivatingOneObservingRangeDoesNotHideObjectStillSeenByAnotherRange()
        {
            LevelFixture level = CreateLevel();
            object firstRange = CreateRangeCollider(level.Level, "First deactivating range");
            object secondRange = CreateRangeCollider(level.Level, "Second deactivating range");
            ObjectFixture target = CreateMapObject("Deactivate shared object", level.Level);
            object visibleObjects = RuntimeAssembly.GetField(level.State, "PlayerVisibleMapObjects");

            RuntimeAssembly.Invoke(firstRange, "OnTriggerEnter2D", target.Collider);
            RuntimeAssembly.Invoke(secondRange, "OnTriggerEnter2D", target.Collider);

            RuntimeAssembly.Invoke(firstRange, "Deactivate");
            Assert.That(CollectionContains(visibleObjects, target.MapObject), Is.True,
                "Deactivating one range must release only that range's visibility ownership.");

            RuntimeAssembly.Invoke(secondRange, "Deactivate");
            Assert.That(CollectionContains(visibleObjects, target.MapObject), Is.False,
                "The final observing range deactivation should remove global visibility.");
        }

        [Test]
        public void MultipleColliderContactsFromOneMapObjectRequireFinalContactExit()
        {
            LevelFixture level = CreateLevel();
            object range = CreateRangeCollider(level.Level, "Multi-contact range");
            ObjectFixture target = CreateMapObject("Multi-collider visible object", level.Level, includeSecondCollider: true);
            object visibleObjects = RuntimeAssembly.GetField(level.State, "PlayerVisibleMapObjects");

            RuntimeAssembly.Invoke(range, "OnTriggerEnter2D", target.Collider);
            RuntimeAssembly.Invoke(range, "OnTriggerEnter2D", target.SecondCollider);
            Assert.That(CollectionContains(visibleObjects, target.MapObject), Is.True);

            RuntimeAssembly.Invoke(range, "OnTriggerExit2D", target.Collider);
            Assert.That(CollectionContains(visibleObjects, target.MapObject), Is.True,
                "One collider contact exiting must not remove visibility while another contact remains.");

            RuntimeAssembly.Invoke(range, "OnTriggerExit2D", target.SecondCollider);
            Assert.That(CollectionContains(visibleObjects, target.MapObject), Is.False,
                "Visibility should be removed when the final contact from this range exits.");
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
            CircleCollider2D collider = rangeObject.AddComponent<CircleCollider2D>();
            collider.isTrigger = true;
            object rangeCollider = rangeObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.RangeCollider"));
            RuntimeAssembly.SetField(rangeCollider, "Weapon", weapon);
            RuntimeAssembly.SetField(rangeCollider, "Collider", collider);
            return rangeCollider;
        }

        private ObjectFixture CreateMapObject(string name, object level, bool includeSecondCollider = false)
        {
            GameObject gameObject = CreateObject(name);
            gameObject.tag = "Object";
            Collider2D collider = gameObject.AddComponent<BoxCollider2D>();
            Collider2D secondCollider = includeSecondCollider ? gameObject.AddComponent<CircleCollider2D>() : null;
            object mapObject = gameObject.AddComponent(RuntimeAssembly.GetType("MapObject"));
            RuntimeAssembly.SetField(mapObject, "Id", _nextMapObjectId++);
            RuntimeAssembly.SetField(mapObject, "Level", level);
            return new ObjectFixture(mapObject, collider, secondCollider);
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
            public readonly Collider2D SecondCollider;

            public ObjectFixture(object mapObject, Collider2D collider, Collider2D secondCollider)
            {
                MapObject = mapObject;
                Collider = collider;
                SecondCollider = secondCollider;
            }
        }
    }
}
