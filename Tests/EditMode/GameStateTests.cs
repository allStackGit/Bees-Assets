using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class GameStateTests
    {
        private GameObject _stateObject;
        private object _state;
        private readonly List<GameObject> _shipObjects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _stateObject = new GameObject(nameof(GameStateTests));
            _state = _stateObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_stateObject);
            foreach (GameObject shipObject in _shipObjects)
            {
                UnityEngine.Object.DestroyImmediate(shipObject);
            }
            _shipObjects.Clear();
        }

        [Test]
        public void ResetStateClearsDerivedRegistriesCountersAndFlags()
        {
            ((IDictionary)RuntimeAssembly.GetField(_state, "ShipsById")).Add(101L, null);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(_state, "PlayerVisibleMapObjects"), null);
            ((IDictionary)RuntimeAssembly.GetField(
                _state,
                "OutcomeIdToPastCommandIndex")).Add(202L, 0);
            RuntimeAssembly.SetField(_state, "UserCommands", 3);
            RuntimeAssembly.SetField(_state, "AICommands", 4);
            RuntimeAssembly.SetField(_state, "HasSelectedSquads", true);
            RuntimeAssembly.SetField(_state, "HasWarpGates", true);
            RuntimeAssembly.SetField(_state, "HasBeehives", true);
            RuntimeAssembly.SetField(_state, "IsPaused", true);
            RuntimeAssembly.SetField(_state, "GameOver", true);
            RuntimeAssembly.SetField(_state, "LevelEnded", true);
            RuntimeAssembly.AddToCollection(
                RuntimeAssembly.GetField(_state, "FogOfWarVisions"), null);

            RuntimeAssembly.Invoke(_state, "ResetState");

            Assert.That(RuntimeAssembly.GetCount(
                RuntimeAssembly.GetField(_state, "ShipsById")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(
                RuntimeAssembly.GetField(_state, "PlayerVisibleMapObjects")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(
                _state,
                "OutcomeIdToPastCommandIndex")), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(_state, "UserCommands"), Is.EqualTo(0));
            Assert.That(RuntimeAssembly.GetField(_state, "AICommands"), Is.EqualTo(0));
            Assert.That(RuntimeAssembly.GetField(_state, "HasSelectedSquads"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_state, "HasWarpGates"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_state, "HasBeehives"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_state, "IsPaused"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_state, "GameOver"), Is.False);
            Assert.That(RuntimeAssembly.GetField(_state, "LevelEnded"), Is.False);
            Assert.That(RuntimeAssembly.GetCount(
                RuntimeAssembly.GetField(_state, "FogOfWarVisions")), Is.Zero);
        }

        [Test]
        public void ResetStateCanBeRepeatedWithoutLeakingSessionState()
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                ((IDictionary)RuntimeAssembly.GetField(_state, "ShipsById"))
                    .Add((long)iteration, null);
                RuntimeAssembly.AddToCollection(
                    RuntimeAssembly.GetField(_state, "PlayerVisibleMapObjects"), null);
                RuntimeAssembly.SetField(_state, "GameOver", true);

                RuntimeAssembly.Invoke(_state, "ResetState");

                Assert.That(RuntimeAssembly.GetCount(
                    RuntimeAssembly.GetField(_state, "ShipsById")), Is.Zero,
                    $"ShipsById leaked on iteration {iteration}.");
                Assert.That(RuntimeAssembly.GetCount(
                    RuntimeAssembly.GetField(_state, "PlayerVisibleMapObjects")), Is.Zero,
                    $"PlayerVisibleMapObjects leaked on iteration {iteration}.");
                Assert.That(RuntimeAssembly.GetField(_state, "GameOver"), Is.False,
                    $"GameOver leaked on iteration {iteration}.");
            }
        }

        [Test]
        public void IsSideKilledReturnsTrueWhenSideHasNoShips()
        {
            Assert.That(IsSideKilled(1), Is.True);
        }

        [Test]
        public void IsSideKilledReturnsTrueWhenSideHasOnlyImmobileShips()
        {
            CreateShip(side: 1, isMobile: false);

            Assert.That(IsSideKilled(1), Is.True);
        }

        [Test]
        public void IsSideKilledReturnsFalseWhenSideHasMobileShip()
        {
            CreateShip(side: 1, isMobile: true);

            Assert.That(IsSideKilled(1), Is.False);
        }

        [Test]
        public void AddAndRemoveShipKeepEveryAuthoritativeRegistryInSync()
        {
            Component ship = CreateShipComponent("Registry Ship");
            object fleetShip = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Data.FleetShip");
            RuntimeAssembly.SetField(ship, "Id", 501L);
            RuntimeAssembly.SetField(ship, "FleetShip", fleetShip);

            RuntimeAssembly.Invoke(_state, "AddShip", ship);

            Assert.That(RuntimeAssembly.GetField(fleetShip, "IsLoadedIntoLevel"), Is.True);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.EqualTo(1));
            Assert.That(((IDictionary)RuntimeAssembly.GetField(_state, "ShipsById"))[501L], Is.SameAs(ship));

            RuntimeAssembly.Invoke(_state, "RemoveShip", ship);

            Assert.That(RuntimeAssembly.GetField(fleetShip, "IsLoadedIntoLevel"), Is.False);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsById")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(1));
        }

        [Test]
        public void AddAndRemoveSquadKeepLoadedStateAndReleaseQueueInSync()
        {
            GameObject squadObject = new GameObject("Registry Squad");
            _shipObjects.Add(squadObject);
            Component squad = squadObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            object savedSquad = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(squad, "SavedSquad", savedSquad);
            RuntimeAssembly.SetField(squad, "Side", 1);

            RuntimeAssembly.Invoke(_state, "AddSquad", squad);

            Assert.That(RuntimeAssembly.GetField(savedSquad, "IsLoadedIntoLevel"), Is.True);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Squads")), Is.EqualTo(1));
            Assert.That(((int[])RuntimeAssembly.GetField(_state, "OriginalSquadCounts"))[0], Is.EqualTo(1));

            RuntimeAssembly.Invoke(_state, "RemoveSquad", squad);

            Assert.That(RuntimeAssembly.GetField(savedSquad, "IsLoadedIntoLevel"), Is.False);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Squads")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "SquadsToRelease")), Is.EqualTo(1));
        }

        [Test]
        public void EndKillRemovesShipAndEmptySquadAndQueuesBothForRelease()
        {
            GameObject stageObject = new GameObject("Lifecycle Stage");
            _shipObjects.Add(stageObject);
            Component stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            RuntimeAssembly.SetField(stage, "IsTraining", true);

            GameObject levelObject = new GameObject("Lifecycle Level");
            _shipObjects.Add(levelObject);
            Component level = levelObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            RuntimeAssembly.SetField(level, "Stage", stage);
            RuntimeAssembly.SetField(level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", level);
            RuntimeAssembly.SetField(_state, "Stage", stage);

            GameObject squadObject = new GameObject("Lifecycle Squad");
            _shipObjects.Add(squadObject);
            Component squad = squadObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            object savedSquad = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(squad, "SavedSquad", savedSquad);
            RuntimeAssembly.SetField(squad, "Side", 1);
            RuntimeAssembly.SetField(squad, "Level", level);
            RuntimeAssembly.SetField(squad, "Stage", stage);
            RuntimeAssembly.SetField(squad, "IsDead", false);
            RuntimeAssembly.Invoke(_state, "AddSquad", squad);

            Component ship = CreateShipComponent("Lifecycle Ship");
            object fleetShip = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Data.FleetShip");
            RuntimeAssembly.SetField(ship, "Id", 601L);
            RuntimeAssembly.SetField(ship, "Side", 1);
            RuntimeAssembly.SetField(ship, "FleetShip", fleetShip);
            RuntimeAssembly.SetField(ship, "Squad", squad);
            RuntimeAssembly.SetField(ship, "Level", level);
            RuntimeAssembly.SetField(ship, "Stage", stage);
            RuntimeAssembly.SetField(ship, "Transform", ship.transform);
            RuntimeAssembly.SetField(ship, "Body", ship.gameObject.AddComponent<Rigidbody2D>());
            RuntimeAssembly.SetField(ship, "IsUserControlled", true);
            RuntimeAssembly.SetField(ship, "IsDead", false);
            ((IList)RuntimeAssembly.Invoke(squad, "GetShips")).Add(ship);
            RuntimeAssembly.Invoke(_state, "AddShip", ship);

            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            object originalConfiguration = RuntimeAssembly.GetStaticField(
                configDataType, "Configuration");
            object testConfiguration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(testConfiguration, "UserSide", 1);
            try
            {
                RuntimeAssembly.SetStaticField(
                    configDataType, "Configuration", testConfiguration);
                RuntimeAssembly.Invoke(ship, "Kill", null, null, null, true);
            }
            finally
            {
                RuntimeAssembly.SetStaticField(
                    configDataType, "Configuration", originalConfiguration);
            }

            Assert.That(RuntimeAssembly.GetField(ship, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetField(squad, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Squads")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "SquadsToRelease")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(fleetShip, "IsLoadedIntoLevel"), Is.False);
            Assert.That(RuntimeAssembly.GetField(savedSquad, "IsLoadedIntoLevel"), Is.False);
        }

        private bool IsSideKilled(int side)
        {
            return (bool)RuntimeAssembly.Invoke(_state, "IsSideKilled", side);
        }

        private void CreateShip(int side, bool isMobile)
        {
            Component ship = CreateShipComponent("Bees Test Ship");
            RuntimeAssembly.SetField(ship, "Side", side);
            RuntimeAssembly.SetField(ship, "IsMobile", isMobile);
            ((IList)RuntimeAssembly.GetField(_state, "Ships")).Add(ship);
        }

        private Component CreateShipComponent(string name)
        {
            GameObject shipObject = new GameObject(name);
            Component ship = shipObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            _shipObjects.Add(shipObject);
            return ship;
        }
    }
}
