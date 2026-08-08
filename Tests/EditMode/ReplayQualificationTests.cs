using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ReplayQualificationTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private Type _eventType;
        private Type _adapterType;
        private Type _snapshotType;

        [SetUp]
        public void SetUp()
        {
            _eventType = RuntimeAssembly.GetType("Assets.Scripts.Levels.SimulationReplayEvent");
            _adapterType = RuntimeAssembly.GetType("Assets.Scripts.Levels.SimulationReplayEventAdapter");
            _snapshotType = RuntimeAssembly.GetType("Assets.Scripts.Levels.SimulationStateSnapshot");
        }

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
        public void CurrentUserCommandAndMovePayloadsRoundTripThroughPlaybackAdapters()
        {
            object commandEvent = Activator.CreateInstance(_eventType, new object[]
            {
                12L, "user-command", "41|Aggressive|93"
            });
            object command = RuntimeAssembly.InvokeStatic(_adapterType, "ParseUserCommand", commandEvent);
            Assert.That(RuntimeAssembly.GetField(command, "SquadItemId"), Is.EqualTo(41));
            Assert.That(RuntimeAssembly.GetField(command, "CommandType").ToString(), Is.EqualTo("Aggressive"));
            Assert.That(RuntimeAssembly.GetField(command, "EnemySquadItemId"), Is.EqualTo(93));

            object moveEvent = Activator.CreateInstance(_eventType, new object[]
            {
                13L, "user-move", "41,42,99|-12.5|33.25"
            });
            object move = RuntimeAssembly.InvokeStatic(_adapterType, "ParseUserMove", moveEvent);
            var ids = (System.Collections.IEnumerable)RuntimeAssembly.GetField(move, "SquadItemIds");
            CollectionAssert.AreEqual(new[] { 41, 42, 99 }, ToIntList(ids));
            Vector2 destination = (Vector2)RuntimeAssembly.GetField(move, "Destination");
            Assert.That(destination.x, Is.EqualTo(-12.5f));
            Assert.That(destination.y, Is.EqualTo(33.25f));
        }

        [TestCase("user-command", "41|Aggressive", "ParseUserCommand")]
        [TestCase("user-command", "x|Aggressive|2", "ParseUserCommand")]
        [TestCase("user-move", "|-1|2", "ParseUserMove")]
        [TestCase("user-move", "1|not-a-number|2", "ParseUserMove")]
        public void PlaybackAdaptersRejectMalformedRecordedInputs(string kind, string payload, string method)
        {
            object replayEvent = Activator.CreateInstance(_eventType, new object[] { 1L, kind, payload });
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.InvokeStatic(_adapterType, method, replayEvent));
            Assert.That(exception.InnerException, Is.TypeOf<FormatException>());
        }

        [Test]
        public void HiveMindPlaybackKeepsServerJsonOpaque()
        {
            const string payload = "{\"Hash\":123,\"Strategy\":\"Aggressive\",\"nested\":{\"x\":1}}";
            object replayEvent = Activator.CreateInstance(_eventType, new object[]
            {
                7L, "hivemind-command-response", payload
            });
            Assert.That(RuntimeAssembly.InvokeStatic(_adapterType, "GetOpaqueServerPayload", replayEvent),
                Is.EqualTo(payload));
        }

        [Test]
        public void UnknownReplayKindsFailClosedInsteadOfBeingSilentlyIgnored()
        {
            object replayEvent = Activator.CreateInstance(_eventType, new object[]
            {
                7L, "future-unhandled-event", "payload"
            });

            Type replayUserCommandType = RuntimeAssembly.GetType("Assets.Scripts.Levels.ReplayUserCommand");
            Type replayUserMoveType = RuntimeAssembly.GetType("Assets.Scripts.Levels.ReplayUserMove");
            Delegate command = CreateIgnoringDelegate(replayUserCommandType);
            Delegate move = CreateIgnoringDelegate(replayUserMoveType);
            Action<string> stringHandler = _ => { };

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                _adapterType.GetMethod("Route", BindingFlags.Public | BindingFlags.Static).Invoke(
                    null,
                    new object[] { replayEvent, command, move, stringHandler, stringHandler }));
            Assert.That(exception.InnerException, Is.TypeOf<NotSupportedException>());
        }

        [Test]
        public void CanonicalStateCheckpointIgnoresRegistryInsertionOrderButDetectsStateChanges()
        {
            SnapshotFixture first = CreateSnapshotFixture(reverseInsertion: false);
            SnapshotFixture second = CreateSnapshotFixture(reverseInsertion: true);

            string firstSnapshot = Capture(first.Level);
            string secondSnapshot = Capture(second.Level);
            Assert.That(secondSnapshot, Is.EqualTo(firstSnapshot),
                "Stable-ID ordering must make replay checkpoints independent of HashSet/dictionary insertion order.");

            RuntimeAssembly.SetField(second.ShipOne, "Health", 73);
            string changedHealth = Capture(second.Level);
            Assert.That(changedHealth, Is.Not.EqualTo(firstSnapshot));

            RuntimeAssembly.SetField(second.ShipOne, "Health", 100);
            ((Component)second.ShipOne).transform.localPosition += new Vector3(0.25f, 0f, 0f);
            string changedPosition = Capture(second.Level);
            Assert.That(changedPosition, Is.Not.EqualTo(firstSnapshot));
        }

        private string Capture(object level)
        {
            object snapshot = RuntimeAssembly.InvokeStatic(_snapshotType, "Capture", level);
            return (string)RuntimeAssembly.GetField(snapshot, "CanonicalState");
        }

        private SnapshotFixture CreateSnapshotFixture(bool reverseInsertion)
        {
            GameObject levelObject = new GameObject("Replay checkpoint Level " + reverseInsertion);
            _objects.Add(levelObject);
            object level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            object state = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(level, "State", state);
            RuntimeAssembly.SetField(state, "Level", level);

            object first = CreateSnapshotShip("Replay ship 10", 10L, 1, new Vector2(-5f, 7f));
            object second = CreateSnapshotShip("Replay ship 20", 20L, 2, new Vector2(8f, -3f));
            RuntimeAssembly.SetField(first, "Level", level);
            RuntimeAssembly.SetField(second, "Level", level);

            if (reverseInsertion)
            {
                RuntimeAssembly.Invoke(state, "AddShip", second);
                RuntimeAssembly.Invoke(state, "AddShip", first);
            }
            else
            {
                RuntimeAssembly.Invoke(state, "AddShip", first);
                RuntimeAssembly.Invoke(state, "AddShip", second);
            }

            return new SnapshotFixture(level, first);
        }

        private object CreateSnapshotShip(string name, long id, int side, Vector2 position)
        {
            GameObject shipObject = new GameObject(name);
            shipObject.transform.localPosition = position;
            _objects.Add(shipObject);
            object ship = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            ((Behaviour)ship).enabled = false;
            RuntimeAssembly.SetField(ship, "Id", id);
            RuntimeAssembly.SetField(ship, "Side", side);
            RuntimeAssembly.SetField(ship, "Health", 100);
            RuntimeAssembly.SetField(ship, "Tsv", 100);
            RuntimeAssembly.SetField(ship, "IsDead", false);
            RuntimeAssembly.SetField(ship, "Transform", shipObject.transform);
            Rigidbody2D body = shipObject.AddComponent<Rigidbody2D>();
            body.gravityScale = 0f;
            body.linearVelocity = new Vector2(id == 10 ? 2f : -1f, id == 10 ? -3f : 4f);
            RuntimeAssembly.SetField(ship, "Body", body);
            object fleetShip = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.FleetShip");
            RuntimeAssembly.SetField(fleetShip, "Id", id);
            RuntimeAssembly.SetField(ship, "FleetShip", fleetShip);
            return ship;
        }

        private static List<int> ToIntList(System.Collections.IEnumerable values)
        {
            var result = new List<int>();
            foreach (object value in values)
            {
                result.Add((int)value);
            }
            return result;
        }

        private static Delegate CreateIgnoringDelegate(Type argumentType)
        {
            Type actionType = typeof(Action<>).MakeGenericType(argumentType);
            MethodInfo method = typeof(ReplayQualificationTests).GetMethod(
                nameof(Ignore), BindingFlags.Static | BindingFlags.NonPublic);
            return Delegate.CreateDelegate(actionType, method.MakeGenericMethod(argumentType));
        }

        private static void Ignore<T>(T value)
        {
        }

        private sealed class SnapshotFixture
        {
            public readonly object Level;
            public readonly object ShipOne;

            public SnapshotFixture(object level, object shipOne)
            {
                Level = level;
                ShipOne = shipOne;
            }
        }
    }
}
