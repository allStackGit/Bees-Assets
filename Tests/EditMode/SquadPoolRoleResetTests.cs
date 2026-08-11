using System.Collections;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadPoolRoleResetTests
    {
        private GameObject _stateObject;
        private GameObject _squadObject;
        private object _state;
        private Component _squad;

        [SetUp]
        public void SetUp()
        {
            _stateObject = new GameObject(nameof(SquadPoolRoleResetTests) + " State");
            _state = _stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));

            _squadObject = new GameObject(nameof(SquadPoolRoleResetTests) + " Squad");
            _squad = _squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            RuntimeAssembly.SetField(_squad, "IsMinionSquad", true);
            ((IList)RuntimeAssembly.GetField(_state, "Squads")).Add(_squad);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_squadObject);
            Object.DestroyImmediate(_stateObject);
        }

        [Test]
        public void RemovingMinionSquadClearsTransientRoleBeforePooling()
        {
            RuntimeAssembly.Invoke(_state, "RemoveSquad", _squad);

            Assert.That(RuntimeAssembly.GetField(_squad, "IsMinionSquad"), Is.False,
                "A pooled ordinary Squad wrapper must not retain minion ownership semantics.");
            Assert.That(((IList)RuntimeAssembly.GetField(_state, "Squads")).Contains(_squad), Is.False);
            Assert.That(((IList)RuntimeAssembly.GetField(_state, "SquadsToRelease")).Contains(_squad), Is.True);
        }
    }
}
