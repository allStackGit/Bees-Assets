using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CombatScenarioQualificationTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private GameObject _stageObject;
        private GameObject _levelObject;
        private object _stage;
        private object _level;
        private object _state;
        private Type _configDataType;
        private object _originalConfiguration;
        private object _originalShipInfo;
        private long _nextShipId;

        [SetUp]
        public void SetUp()
        {
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _originalConfiguration = RuntimeAssembly.GetStaticField(_configDataType, "Configuration");
            _originalShipInfo = RuntimeAssembly.GetStaticField(_configDataType, "ShipInfo");
            InstallConfigurationAndShipStats();
            _nextShipId = 1000;

            _stageObject = new GameObject(nameof(CombatScenarioQualificationTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            RuntimeAssembly.SetField(_stage, "IsTraining", true);
            RuntimeAssembly.SetField(_stage, "IsRendering", false);
            RuntimeAssembly.SetField(_stage, "ReplaceDeadShips", false);
            RuntimeAssembly.SetField(_stage, "MakeShotsHarmless", false);
            ((Behaviour)_stage).enabled = false;

            _levelObject = new GameObject(nameof(CombatScenarioQualificationTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(_level, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            RuntimeAssembly.SetField(_state, "Stage", _stage);
            ((Behaviour)_level).enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", _originalConfiguration);
            RuntimeAssembly.SetStaticField(_configDataType, "ShipInfo", _originalShipInfo);
            foreach (GameObject gameObject in _objects)
            {
                if (gameObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                }
            }
            UnityEngine.Object.DestroyImmediate(_levelObject);
            UnityEngine.Object.DestroyImmediate(_stageObject);
            _objects.Clear();
        }

        [Test]
        public void OpposingLethalHitsInSameSimulationStepFinalizeEachDeathExactlyOnce()
        {
            object userSavedSquad = CreateSavedSquad("User");
            object aiSavedSquad = CreateSavedSquad("AI");
            object userSquad = CreateSquad("User squad", 1, userSavedSquad);
            object aiSquad = CreateSquad("AI squad", 2, aiSavedSquad);

            ShipFixture userShooter = CreateShip("User shooter", 1, userSquad);
            CreateShip("User survivor", 1, userSquad);
            ShipFixture aiVictim = CreateShip("AI victim", 2, aiSquad);
            ShipFixture aiShooter = CreateShip("AI shooter", 2, aiSquad);

            ApplyDamage(userShooter, userSavedSquad, aiVictim.Ship, 1000);
            ApplyDamage(aiShooter, aiSavedSquad, userShooter.Ship, 1000);

            Assert.That(RuntimeAssembly.GetField(userShooter.Ship, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetField(aiVictim.Ship, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.EqualTo(2),
                "Both squads should retain exactly their surviving ship.");
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(2));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "SquadsToRelease")), Is.Zero);
            Assert.That(RuntimeAssembly.GetField(userShooter.FleetShip, "Kills"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(aiShooter.FleetShip, "Kills"), Is.EqualTo(1));
            Assert.That(GetStat(userSavedSquad, "Kills"), Is.EqualTo(1));
            Assert.That(GetStat(aiSavedSquad, "Kills"), Is.EqualTo(1));

            ApplyDamage(userShooter, userSavedSquad, aiVictim.Ship, 1000);
            ApplyDamage(aiShooter, aiSavedSquad, userShooter.Ship, 1000);

            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(2),
                "Repeated lethal callbacks must not enqueue a second release.");
            Assert.That(RuntimeAssembly.GetField(userShooter.FleetShip, "Kills"), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(aiShooter.FleetShip, "Kills"), Is.EqualTo(1));
        }

        [Test]
        public void ManyShipLethalSweepKeepsRegistriesStatisticsAndFinalSquadTeardownConsistent()
        {
            const int targetCount = 24;
            object userSavedSquad = CreateSavedSquad("User many-ship attacker");
            object aiSavedSquad = CreateSavedSquad("AI many-ship target");
            object userSquad = CreateSquad("User many-ship squad", 1, userSavedSquad);
            object aiSquad = CreateSquad("AI many-ship squad", 2, aiSavedSquad);
            ShipFixture attacker = CreateShip("Attacker", 1, userSquad);

            var targets = new List<ShipFixture>();
            for (int index = 0; index < targetCount; index++)
            {
                targets.Add(CreateShip("Target " + index, 2, aiSquad));
            }

            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.EqualTo(targetCount + 1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Squads")), Is.EqualTo(2));

            foreach (ShipFixture target in targets)
            {
                ApplyDamage(attacker, userSavedSquad, target.Ship, 1000);
            }

            Assert.That(RuntimeAssembly.GetField(attacker.FleetShip, "Kills"), Is.EqualTo(targetCount));
            Assert.That(GetStat(userSavedSquad, "Kills"), Is.EqualTo(targetCount));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Ships")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsById")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(targetCount));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "Squads")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "SquadsToRelease")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(aiSquad, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetField(aiSavedSquad, "IsLoadedIntoLevel"), Is.False);

            foreach (ShipFixture target in targets)
            {
                Assert.That(RuntimeAssembly.GetField(target.Ship, "IsDead"), Is.True);
                Assert.That(RuntimeAssembly.GetField(target.FleetShip, "IsLoadedIntoLevel"), Is.False);
            }

            foreach (ShipFixture target in targets)
            {
                ApplyDamage(attacker, userSavedSquad, target.Ship, 1000);
            }
            Assert.That(RuntimeAssembly.GetField(attacker.FleetShip, "Kills"), Is.EqualTo(targetCount));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "ShipsToRelease")), Is.EqualTo(targetCount));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "SquadsToRelease")), Is.EqualTo(1));
        }

        private void ApplyDamage(ShipFixture attacker, object attackerSavedSquad, object target, int power)
        {
            RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"),
                "LogAttackingDamage",
                power,
                attacker.Ship,
                attacker.FleetShip,
                attackerSavedSquad,
                target);
        }

        private object CreateSavedSquad(string name)
        {
            object savedSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(savedSquad, "Name", name);
            RuntimeAssembly.SetField(savedSquad, "Stats", Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.SquadStatBlock"),
                new object[] { "Tester", 0, 0, 0, 0, 0, 0 }));
            return savedSquad;
        }

        private object CreateSquad(string name, int side, object savedSquad)
        {
            GameObject squadObject = new GameObject(name);
            _objects.Add(squadObject);
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

        private ShipFixture CreateShip(string name, int side, object squad)
        {
            long id = _nextShipId++;
            object fleetShip = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.FleetShip");
            RuntimeAssembly.SetField(fleetShip, "Id", id);
            RuntimeAssembly.SetField(fleetShip, "Side", side);
            RuntimeAssembly.SetField(fleetShip, "Name", name);
            RuntimeAssembly.SetField(fleetShip, "Type", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), "Honeybee"));
            RuntimeAssembly.SetField(fleetShip, "MaxHealth", 100);
            RuntimeAssembly.SetField(fleetShip, "Health", 100);

            GameObject shipObject = new GameObject(name);
            _objects.Add(shipObject);
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
            return new ShipFixture(ship, fleetShip);
        }

        private int GetStat(object savedSquad, string fieldName)
        {
            return (int)RuntimeAssembly.GetField(RuntimeAssembly.GetField(savedSquad, "Stats"), fieldName);
        }

        private void InstallConfigurationAndShipStats()
        {
            object configuration = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Settings.Configuration");
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
            object statBlock = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Settings.ShipStatBlock");
            RuntimeAssembly.SetField(statBlock, "Type", honeybee);
            RuntimeAssembly.SetField(statBlock, "Health", 100);
            RuntimeAssembly.SetField(statBlock, "Tsv", 100);
            dictionary.Add(honeybee, statBlock);
            RuntimeAssembly.SetField(stats, "ShipStatsList", dictionary);
            RuntimeAssembly.SetStaticField(_configDataType, "ShipInfo", stats);
        }

        private sealed class ShipFixture
        {
            public readonly object Ship;
            public readonly object FleetShip;

            public ShipFixture(object ship, object fleetShip)
            {
                Ship = ship;
                FleetShip = fleetShip;
            }
        }
    }
}
