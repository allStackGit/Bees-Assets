using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MapObjectVisibilityTrackerTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

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
        }

        [Test]
        public void DestroyingVisibleMapObjectRemovesItFromGameStateImmediately()
        {
            Fixture fixture = CreateFixture();
            object visibleObjects = RuntimeAssembly.GetField(fixture.State, "PlayerVisibleMapObjects");

            RuntimeAssembly.Invoke(fixture.Range, "OnTriggerEnter2D", fixture.MapCollider);
            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(1));

            Object.DestroyImmediate(fixture.MapObjectGameObject);

            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(0),
                "Destroying a visible MapObject must not leave a destroyed reference in player visibility.");
        }

        [Test]
        public void VisibilityTrackerSelfHealsIfGameStateWasResetWhileObjectSurvived()
        {
            Fixture fixture = CreateFixture();
            object visibleObjects = RuntimeAssembly.GetField(fixture.State, "PlayerVisibleMapObjects");

            RuntimeAssembly.Invoke(fixture.Range, "OnTriggerEnter2D", fixture.MapCollider);
            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(1));

            RuntimeAssembly.Invoke(fixture.State, "ResetState");
            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(0));

            // The physical contact can be re-reported after a reset/rebuild. The tracker
            // must discard stale pre-reset source ownership when the authoritative set is empty.
            RuntimeAssembly.Invoke(fixture.Range, "OnTriggerExit2D", fixture.MapCollider);
            RuntimeAssembly.Invoke(fixture.Range, "OnTriggerEnter2D", fixture.MapCollider);
            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(1));

            RuntimeAssembly.Invoke(fixture.Range, "OnTriggerExit2D", fixture.MapCollider);
            Assert.That(RuntimeAssembly.GetCount(visibleObjects), Is.EqualTo(0));
        }

        private Fixture CreateFixture()
        {
            GameObject levelObject = CreateObject("Tracker Level");
            object level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            object state = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(level, "State", state);
            RuntimeAssembly.SetField(state, "Level", level);

            GameObject shipObject = CreateObject("Tracker Ship");
            object ship = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            RuntimeAssembly.SetField(ship, "Level", level);

            GameObject weaponObject = CreateObject("Tracker Weapon");
            object weapon = weaponObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.Weapon"));
            RuntimeAssembly.SetField(weapon, "Ship", ship);

            GameObject rangeObject = CreateObject("Tracker Range");
            CircleCollider2D rangePhysics = rangeObject.AddComponent<CircleCollider2D>();
            rangePhysics.isTrigger = true;
            object range = rangeObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.RangeCollider"));
            RuntimeAssembly.SetField(range, "Weapon", weapon);
            RuntimeAssembly.SetField(range, "Collider", rangePhysics);

            GameObject mapObjectGameObject = CreateObject("Tracker Map Object");
            mapObjectGameObject.tag = "Object";
            BoxCollider2D mapCollider = mapObjectGameObject.AddComponent<BoxCollider2D>();
            object mapObject = mapObjectGameObject.AddComponent(RuntimeAssembly.GetType("MapObject"));
            RuntimeAssembly.SetField(mapObject, "Id", 41001);
            RuntimeAssembly.SetField(mapObject, "Level", level);

            return new Fixture(state, range, mapObjectGameObject, mapCollider);
        }

        private GameObject CreateObject(string name)
        {
            GameObject gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }

        private sealed class Fixture
        {
            public readonly object State;
            public readonly object Range;
            public readonly GameObject MapObjectGameObject;
            public readonly Collider2D MapCollider;

            public Fixture(object state, object range, GameObject mapObjectGameObject, Collider2D mapCollider)
            {
                State = state;
                Range = range;
                MapObjectGameObject = mapObjectGameObject;
                MapCollider = mapCollider;
            }
        }
    }
}
