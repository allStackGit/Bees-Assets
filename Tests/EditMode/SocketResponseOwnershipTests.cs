using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SocketResponseOwnershipTests
    {
        private object _socket;
        private object _standingRequests;
        private GameObject _levelObject;
        private GameObject _squadObject;
        private Component _level;
        private Component _state;
        private Component _squad;
        private Type _requestTypes;

        [SetUp]
        public void SetUp()
        {
            Type serverRequestType = RuntimeAssembly.GetType("Assets.Scripts.Server.ServerRequest");
            _socket = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Server.Socket");
            _standingRequests = Activator.CreateInstance(
                typeof(System.Collections.Generic.HashSet<>).MakeGenericType(serverRequestType));
            RuntimeAssembly.SetField(_socket, "StandingRequests", _standingRequests);
            RuntimeAssembly.SetField(_socket, "HandledRequests", new System.Collections.Generic.HashSet<long>());
            _requestTypes = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+RequestTypes");

            _levelObject = new GameObject(nameof(SocketResponseOwnershipTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            ((Behaviour)_level).enabled = false;

            _squadObject = new GameObject(nameof(SocketResponseOwnershipTests) + " Squad");
            _squad = _squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            RuntimeAssembly.SetField(_squad, "Id", 500L);
            RuntimeAssembly.SetField(_squad, "ItemId", 17);
            RuntimeAssembly.SetField(_squad, "IsDead", false);
            ((Behaviour)_squad).enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_squadObject);
            UnityEngine.Object.DestroyImmediate(_levelObject);
        }

        [Test]
        public void DuplicateResponseHashCanBeClaimedOnlyOnce()
        {
            Assert.That(RuntimeAssembly.Invoke(_socket, "TryClaimResponse", 7001L), Is.True);
            Assert.That(RuntimeAssembly.Invoke(_socket, "TryClaimResponse", 7001L), Is.False);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_socket, "HandledRequests")), Is.EqualTo(1));
        }

        [Test]
        public void StandingRequestIsConsumedOnlyByMatchingResponseType()
        {
            object request = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Server.CommandRequest");
            SetFieldIncludingBase(request, "Hash", 8001L);
            SetFieldIncludingBase(request, "Type", Enum.Parse(_requestTypes, "GetStrategy"));
            RuntimeAssembly.AddToCollection(_standingRequests, request);

            object mismatch = RuntimeAssembly.Invoke(
                _socket,
                "TakeStandingRequest",
                8001L,
                Enum.Parse(_requestTypes, "GetMatchupStrategy"));
            Assert.That(mismatch, Is.Null);
            Assert.That(RuntimeAssembly.GetCount(_standingRequests), Is.EqualTo(1),
                "A mismatched response removed the request needed by the correct handler.");

            object match = RuntimeAssembly.Invoke(
                _socket,
                "TakeStandingRequest",
                8001L,
                Enum.Parse(_requestTypes, "GetStrategy"));
            Assert.That(match, Is.SameAs(request));
            Assert.That(RuntimeAssembly.GetCount(_standingRequests), Is.Zero);

            Assert.That(RuntimeAssembly.Invoke(
                _socket,
                "TakeStandingRequest",
                9999L,
                Enum.Parse(_requestTypes, "GetStrategy")), Is.Null);
        }

        [Test]
        public void SquadResponseRequiresCurrentRuntimeIdentityAndLiveLevel()
        {
            Assert.That(CanApply(expectedItemId: 17), Is.True);

            RuntimeAssembly.SetField(_squad, "ItemId", 18);
            Assert.That(CanApply(expectedItemId: 17), Is.False,
                "A response from the prior pooled Squad lifecycle was accepted.");

            RuntimeAssembly.SetField(_squad, "ItemId", 17);
            RuntimeAssembly.SetField(_state, "LevelEnded", true);
            Assert.That(CanApply(expectedItemId: 17), Is.False,
                "An ended Level accepted a squad response.");

            RuntimeAssembly.SetField(_state, "LevelEnded", false);
            RuntimeAssembly.SetField(_squad, "IsDead", true);
            Assert.That(CanApply(expectedItemId: 17), Is.False,
                "A dead Squad accepted a response.");
        }

        [Test]
        public void MatchupRequestUsesRuntimeItemIdRatherThanPersistentSquadId()
        {
            object request = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Server.MatchupStrategyRequest");
            SetFieldIncludingBase(request, "Squad", _squad);
            SetFieldIncludingBase(request, "SquadId", 17);

            Assert.That(RuntimeAssembly.Invoke(request, "HasSameSquad"), Is.True,
                "The request did not recognize the runtime Squad lifecycle it captured.");

            RuntimeAssembly.SetField(_squad, "ItemId", 18);
            Assert.That(RuntimeAssembly.Invoke(request, "HasSameSquad"), Is.False,
                "Persistent Squad identity allowed a response to cross pooled lifecycles.");
        }

        private bool CanApply(int expectedItemId)
        {
            return (bool)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType("Assets.Scripts.Server.Socket"),
                "CanApplySquadResponse",
                _level,
                _squad,
                expectedItemId);
        }

        private static void SetFieldIncludingBase(object instance, string fieldName, object value)
        {
            Type type = instance.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(instance, value);
                    return;
                }
                type = type.BaseType;
            }
            throw new MissingFieldException(instance.GetType().FullName, fieldName);
        }
    }
}
