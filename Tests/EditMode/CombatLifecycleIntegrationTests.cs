using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CombatLifecycleIntegrationTests
    {
        private GameObject _stageObject;
        private GameObject _levelObject;
        private readonly List<GameObject> _entityObjects = new List<GameObject>();
        private object _stage;
        private object _level;
        private object _state;
        private object _attacker;
        private object _target;
        private object _targetWingman;
        private object _attackerSquad;
        private object _targetSquad;
        private object _attackerFleetShip;
        private object _targetFleetShip;
        private object _attackerSavedSquad;
        private object _targetSavedSquad;
        private object _attackerCommand;
        private object _targetCommand;
        private object _weapon;
        private Type _configDataType;
        private object _originalConfiguration;
        private object _originalShipInfo;

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _originalConfiguration = RuntimeAssembly.GetStaticField(_configDataType, "Configuration");
            _originalShipInfo = RuntimeAssembly.GetStaticField(_configDataType, "ShipInfo");
            InstallConfigurationAndShipStats();

            _stageObject = new GameObject(nameof(CombatLifecycleIntegrationTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            RuntimeAssembly.SetField(_stage, "IsTraining", true);
            RuntimeAssembly.SetField(_stage, "IsRendering", false);
            RuntimeAssembly.SetField(_stage, "ReplaceDeadShips", false);
            RuntimeAssembly.SetField(_stage, "MakeShotsHarmless", false);
            ((Behaviour)_stage).enabled = false;

            _levelObject = new GameObject(nameof(CombatLifecycleIntegrationTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(_level, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            RuntimeAssembly.SetField(_state, "Stage", _stage);
            ((Behaviour)_level).enabled = false;

            _attackerSavedSquad = CreateSavedSquad("Attackers");
            _targetSavedSquad = CreateSavedSquad("Targets");
            _attackerSquad = CreateSquad("Attacker squad", 1, _attackerSavedSquad);
            _targetSquad = CreateSquad("Target squad", 2, _targetSavedSquad);
            _attackerFleetShip = CreateFleetShip(101, 1, "Attacker");
            _targetFleetShip = CreateFleetShip(202, 2, "Target");
            _attacker = CreateShip("Attacker", 1, 101, _attackerSquad, _attackerFleetShip);
            _target = CreateShip("Target", 2, 202, _targetSquad, _targetFleetShip);
            _targetWingman = CreateShip("Target wingman", 2, 203, _targetSquad,
                CreateFleetShip(203, 2, "Target wingman"));

            _attackerCommand = AttachCommand(_attackerSquad, "Attacker command");
            _targetCommand = AttachCommand(_targetSquad, "Target command");

            GameObject weaponObject = new GameObject("Reverse targeting weapon");
            _entityObjects.Add(weaponObject);
            _weapon = weaponObject.AddComponent(RuntimeAssembly.GetType(
                "Assets.Scripts.Entities.Ships.Weapons.Weapon"));
            RuntimeAssembly.SetField(_weapon, "Ship", _attacker);
            RuntimeAssembly.SetField(_weapon, "Level", _level);
            RuntimeAssembly.SetField(_weapon, "Stage", _stage);
            IDictionary shipsWithinRange = (IDictionary)RuntimeAssembly.GetField(_weapon, "ShipsWithinRange");
            shipsWithinRange.Add(202L, _target);
            RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(_target, "WeaponsThatHaveUsWithinRange"), _weapon);
            RuntimeAssembly.AddToCollection(RuntimeAssembly.GetField(_attacker, "Weapons"), _weapon);
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", _originalConfiguration);
            RuntimeAssembly.SetStaticField(_configDataType, "ShipInfo", _originalShipInfo);
            foreach (GameObject entityObject in _entityObjects)
            {
                UnityEngine.Object.DestroyImmediate(entityObject);
            }
            UnityEngine.Object.DestroyImmediate(_levelObject);
            UnityEngine.Object.DestroyImmediate(_stageObject);
            _entityObjects.Clear();
        }

        [Test]
        public void DamageThenLethalHitUpdatesStatsCommandsCachesAndRegistriesExactlyOnce()
        {
            InvokeDamage(20);

            Assert.That(RuntimeAssembly.GetField(_target, "Health"), Is.EqualTo(80));
            int remainingTsv = (int)RuntimeAssembly.GetField(_target, "Tsv");
            int firstTsvLoss = 100 - remainingTsv;
            Assert.That(firstTsvLoss, Is.GreaterThan(0));
            Assert.That(RuntimeAssembly.GetField(_attackerFleetShip, "DamageDone"), Is.EqualTo(firstTsvLoss));
            Assert.That(RuntimeAssembly.GetField(_targetFleetShip, "DamageReceived"), Is.EqualTo(firstTsvLoss));
            Assert.That(GetStats(_attackerSavedSquad, "DamageDone"), Is.EqualTo(firstTsvLoss));
            Assert.That(GetStats(_targetSavedSquad, "DamageReceived"), Is.EqualTo(firstTsvLoss));
            Assert.That(RuntimeAssembly.GetField(_attackerCommand, "Tsv"), Is.EqualTo((long)firstTsvLoss),
                "A single damage event must adjust the attacker command once.");
            Assert.That(RuntimeAssembly.GetField(_targetCommand, "Tsv"), Is.EqualTo((long)-firstTsvLoss));
            AssertDamageStatus(80, expectedCount: 1);

            InvokeDamage(1000);

            Assert.That(RuntimeAssembly.GetField(_target, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetField(_target, "Health"), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(_attackerFleetShip, "DamageDone"), Is.EqualTo(100));
            Assert.That(RuntimeAssembly.GetField(_targetFleetShip, "DamageReceived"), Is.EqualTo(100));
            Assert.That(RuntimeAssembly.GetField(_attackerFleetShip, "Kills"), Is.EqualTo(1));
            Assert.That(GetStats(_attackerSavedSquad, "Kills"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(_attackerCommand, "Tsv"), Is.EqualTo(100L));
            Assert.That(RuntimeAssembly.GetField(_targetCommand, "Tsv"), Is.EqualTo(-100L));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(1));
            AssertDamageStatus(80, expectedCount: 1);
            Assert.That(((IDictionary)RuntimeAssembly.GetField(_weapon, "ShipsWithinRange")).Contains(202L), Is.False);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_target, "WeaponsThatHaveUsWithinRange")), Is.Zero);

            InvokeDamage(1000);
            Assert.That(RuntimeAssembly.GetField(_attackerFleetShip, "Kills"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(1));
            AssertDamageStatus(80, expectedCount: 1);
        }

        [Test]
        public void HarmlessModePreservesHealthTsvAndStatistics()
        {
            RuntimeAssembly.SetField(_stage, "MakeShotsHarmless", true);
            InvokeDamage(50);

            Assert.That(RuntimeAssembly.GetField(_target, "Health"), Is.EqualTo(100));
            Assert.That(RuntimeAssembly.GetField(_target, "Tsv"), Is.EqualTo(100));
            Assert.That(RuntimeAssembly.GetField(_attackerFleetShip, "DamageDone"), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(_targetFleetShip, "DamageReceived"), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(_attackerCommand, "Tsv"), Is.EqualTo(0L));
            Assert.That(RuntimeAssembly.GetField(_targetCommand, "Tsv"), Is.EqualTo(0L));
            AssertDamageStatus(100, expectedCount: 1);
        }

        private void InvokeDamage(int power)
        {
            RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"),
                "LogAttackingDamage",
                power,
                _attacker,
                _attackerFleetShip,
                _attackerSavedSquad,
                _target);
        }

        private void AssertDamageStatus(int health, int expectedCount)
        {
            Array statuses = (Array)RuntimeAssembly.GetField(_state, "ShipDamageStatuses");
            object attackerStatuses = statuses.GetValue(0);
            Assert.That(RuntimeAssembly.GetCount(attackerStatuses), Is.EqualTo(expectedCount));
            if (expectedCount > 0)
            {
                object status = ((IList)attackerStatuses)[0];
                Assert.That(RuntimeAssembly.GetField(status, "Health"), Is.EqualTo(health));
                Assert.That(RuntimeAssembly.GetField(status, "Ship"), Is.SameAs(_target));
            }
        }

        private object CreateSavedSquad(string name)
        {
            object squad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(squad, "Name", name);
            RuntimeAssembly.SetField(squad, "Stats", Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.SquadStatBlock"),
                new object[] { "Tester", 0, 0, 0, 0, 0, 0 }));
            return squad;
        }

        private object CreateSquad(string name, int side, object savedSquad)
        {
            GameObject squadObject = new GameObject(name);
            _entityObjects.Add(squadObject);
            object squad = squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            RuntimeAssembly.SetField(squad, "Name", name);
            RuntimeAssembly.SetField(squad, "Side", side);
            RuntimeAssembly.SetField(squad, "Level", _level);
            RuntimeAssembly.SetField(squad, "Stage", _stage);
            RuntimeAssembly.SetField(squad, "SavedSquad", savedSquad);
            RuntimeAssembly.SetField(squad, "IsUserControlled", false);
            RuntimeAssembly.SetField(squad, "IsDead", false);
            RuntimeAssembly.Invoke(_state, "AddSquad", squad);
            return squad;
        }

        private object CreateFleetShip(long id, int side, string name)
        {
            object ship = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.FleetShip");
            RuntimeAssembly.SetField(ship, "Id", id);
            RuntimeAssembly.SetField(ship, "Side", side);
            RuntimeAssembly.SetField(ship, "Name", name);
            RuntimeAssembly.SetField(ship, "Type", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), "Honeybee"));
            RuntimeAssembly.SetField(ship, "MaxHealth", 100);
            RuntimeAssembly.SetField(ship, "Health", 100);
            return ship;
        }

        private object CreateShip(string name, int side, long id, object squad, object fleetShip)
        {
            GameObject shipObject = new GameObject(name);
            _entityObjects.Add(shipObject);
            object ship = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            ((Behaviour)ship).enabled = false;
            RuntimeAssembly.SetField(ship, "Name", name);
            RuntimeAssembly.SetField(ship, "Id", id);
            RuntimeAssembly.SetField(ship, "Side", side);
            RuntimeAssembly.SetField(ship, "ShipType", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), "Honeybee"));
            RuntimeAssembly.SetField(ship, "Health", 100);
            RuntimeAssembly.SetField(ship, "MaxHealth", 100);
            RuntimeAssembly.SetField(ship, "OriginalHealth", 100);
            RuntimeAssembly.SetField(ship, "Tsv", 100);
            RuntimeAssembly.SetField(ship, "Level", _level);
            RuntimeAssembly.SetField(ship, "Stage", _stage);
            RuntimeAssembly.SetField(ship, "Squad", squad);
            RuntimeAssembly.SetField(ship, "FleetShip", fleetShip);
            RuntimeAssembly.SetField(ship, "Transform", shipObject.transform);
            RuntimeAssembly.SetField(ship, "Body", shipObject.AddComponent<Rigidbody2D>());
            RuntimeAssembly.SetField(ship, "IsUserControlled", true);
            RuntimeAssembly.SetField(ship, "IsDead", false);
            RuntimeAssembly.SetField(ship, "Weapons", Activator.CreateInstance(
                typeof(List<>).MakeGenericType(RuntimeAssembly.GetType(
                    "Assets.Scripts.Entities.Ships.Weapons.Weapon"))));
            RuntimeAssembly.AddToCollection(RuntimeAssembly.Invoke(squad, "GetShips"), ship);
            RuntimeAssembly.Invoke(_state, "AddShip", ship);
            return ship;
        }

        private object AttachCommand(object squad, string name)
        {
            GameObject commandObject = new GameObject(name);
            _entityObjects.Add(commandObject);
            object command = commandObject.AddComponent(RuntimeAssembly.GetType(
                "Assets.Scripts.Levels.Commands.Command"));
            RuntimeAssembly.SetField(command, "CommandType", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+CommandTypes"), "Aggressive"));
            RuntimeAssembly.SetField(command, "Level", _level);
            RuntimeAssembly.SetField(command, "Stage", _stage);
            RuntimeAssembly.Invoke(squad, "SetCommand", command);
            RuntimeAssembly.SetField(squad, "HasCommand", true);
            return command;
        }

        private int GetStats(object savedSquad, string field)
        {
            return (int)RuntimeAssembly.GetField(RuntimeAssembly.GetField(savedSquad, "Stats"), field);
        }

        private void InstallConfigurationAndShipStats()
        {
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(configuration, "UserSide", 1);
            RuntimeAssembly.SetField(configuration, "AISide", 2);
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", configuration);

            Type shipTypes = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            object honeybee = Enum.Parse(shipTypes, "Honeybee");
            object stats = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Settings.ShipStats");
            Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(
                shipTypes,
                RuntimeAssembly.GetType("Assets.Scripts.Settings.ShipStatBlock"));
            IDictionary dictionary = (IDictionary)Activator.CreateInstance(dictionaryType);
            object statBlock = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.ShipStatBlock");
            RuntimeAssembly.SetField(statBlock, "Type", honeybee);
            RuntimeAssembly.SetField(statBlock, "Health", 100);
            RuntimeAssembly.SetField(statBlock, "Tsv", 100);
            dictionary.Add(honeybee, statBlock);
            RuntimeAssembly.SetField(stats, "ShipStatsList", dictionary);
            RuntimeAssembly.SetStaticField(_configDataType, "ShipInfo", stats);
        }
    }
}
