using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TransientSquadNumberingTests
    {
        private GameObject _stateObject;
        private object _state;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _stateObject = new GameObject(nameof(TransientSquadNumberingTests));
            _state = _stateObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_stateObject);
            foreach (GameObject obj in _objects)
            {
                Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void MinionSquadsGetUniqueRuntimeNumbersWithoutExpandingNormalSquadCount()
        {
            Component normal = CreateSquad("Normal", side: 1, squadNumber: 1, isMinion: false);
            RuntimeAssembly.Invoke(_state, "AddSquad", normal);

            Component firstMinion = CreateSquad("Minion 1", side: 1, squadNumber: 2, isMinion: true);
            RuntimeAssembly.Invoke(_state, "AddSquad", firstMinion);

            Component secondMinion = CreateSquad("Minion 2", side: 1, squadNumber: 2, isMinion: true);
            RuntimeAssembly.Invoke(_state, "AddSquad", secondMinion);

            Assert.That(((int[])RuntimeAssembly.GetField(_state, "OriginalSquadCounts"))[0], Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(firstMinion, "SquadNumber"), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetField(secondMinion, "SquadNumber"), Is.EqualTo(3));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Squads")), Is.EqualTo(3));
        }

        private Component CreateSquad(string name, int side, int squadNumber, bool isMinion)
        {
            GameObject obj = new GameObject(name);
            _objects.Add(obj);
            Component squad = obj.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            RuntimeAssembly.SetField(squad, "Side", side);
            RuntimeAssembly.SetField(squad, "SquadNumber", squadNumber);
            RuntimeAssembly.SetField(squad, "IsMinionSquad", isMinion);
            RuntimeAssembly.SetField(squad, "SavedSquad", RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad"));
            return squad;
        }
    }
}
