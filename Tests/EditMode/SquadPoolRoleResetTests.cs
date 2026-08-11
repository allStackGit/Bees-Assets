using Assets.Scripts.Levels;
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
        private GameState _state;
        private Squad _squad;

        [SetUp]
        public void SetUp()
        {
            _stateObject = new GameObject(nameof(SquadPoolRoleResetTests) + " State");
            _state = _stateObject.AddComponent<GameState>();

            _squadObject = new GameObject(nameof(SquadPoolRoleResetTests) + " Squad");
            _squad = _squadObject.AddComponent<Squad>();
            _squad.IsMinionSquad = true;
            _state.Squads.Add(_squad);
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
            _state.RemoveSquad(_squad);

            Assert.That(_squad.IsMinionSquad, Is.False,
                "A pooled ordinary Squad wrapper must not retain minion ownership semantics.");
            Assert.That(_state.Squads, Does.Not.Contain(_squad));
            Assert.That(_state.SquadsToRelease, Does.Contain(_squad));
        }
    }
}
