using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SingleShipSquadDeathTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

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
        public void KillingOnlyShipTearsDownShipSquadCommandAndHivemindObserver()
        {
            GameObject stageObject = CreateObject("Single ship death Stage");
            object stage = stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            RuntimeAssembly.SetField(stage, "IsTraining", true);
            RuntimeAssembly.SetField(stage, "IsRendering", false);

            GameObject levelObject = CreateObject("Single ship death Level");
            object level = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            object state = levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(level, "Stage", stage);
            RuntimeAssembly.SetField(level, "State", state);
            RuntimeAssembly.SetField(state, "Level", level);
            RuntimeAssembly.SetField(state, "Stage", stage);
            ((Behaviour)level).enabled = false;

            object savedSquad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            RuntimeAssembly.SetField(savedSquad, "Name", "Single Bee squad");

            GameObject squadObject = CreateObject("Single Bee squad");
            object squad = squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            RuntimeAssembly.SetField(squad, "Name", "Single Bee squad");
            RuntimeAssembly.SetField(squad, "Side", 2);
            RuntimeAssembly.SetField(squad, "Level", level);
            RuntimeAssembly.SetField(squad, "Stage", stage);
            RuntimeAssembly.SetField(squad, "SavedSquad", savedSquad);
            RuntimeAssembly.SetField(squad, "IsDead", false);
            RuntimeAssembly.SetField(squad, "IsUserControlled", false);
            RuntimeAssembly.SetField(squad, "IsHiveMindControlled", true);
            RuntimeAssembly.Invoke(state, "AddSquad", squad);

            object fleetShip = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.FleetShip");
            RuntimeAssembly.SetField(fleetShip, "Id", 9001L);
            RuntimeAssembly.SetField(fleetShip, "Side", 2);
            RuntimeAssembly.SetField(fleetShip, "Name", "Single Bee");

            GameObject shipObject = CreateObject("Single Bee");
            object ship = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            Rigidbody2D body = shipObject.AddComponent<Rigidbody2D>();
            CircleCollider2D visionCollider = shipObject.AddComponent<CircleCollider2D>();
            object hivemindVision = shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Weapons.HiveMindVision"));
            RuntimeAssembly.SetField(hivemindVision, "Collider", visionCollider);
            RuntimeAssembly.SetField(hivemindVision, "Ship", ship);

            RuntimeAssembly.SetField(ship, "Name", "Single Bee");
            RuntimeAssembly.SetField(ship, "Id", 9001L);
            RuntimeAssembly.SetField(ship, "Side", 2);
            RuntimeAssembly.SetField(ship, "Level", level);
            RuntimeAssembly.SetField(ship, "Stage", stage);
            RuntimeAssembly.SetField(ship, "Squad", squad);
            RuntimeAssembly.SetField(ship, "FleetShip", fleetShip);
            RuntimeAssembly.SetField(ship, "Body", body);
            RuntimeAssembly.SetField(ship, "Transform", shipObject.transform);
            RuntimeAssembly.SetField(ship, "HiveMindVision", hivemindVision);
            RuntimeAssembly.SetField(ship, "IsDead", false);
            RuntimeAssembly.SetField(ship, "IsUserControlled", false);
            RuntimeAssembly.SetField(ship, "IsHiveMindControlled", true);
            RuntimeAssembly.SetField(ship, "HasWeapons", false);
            RuntimeAssembly.SetField(ship, "HasProximityCollider", false);
            RuntimeAssembly.Invoke(squad, "AddShip", ship);
            RuntimeAssembly.Invoke(state, "AddShip", ship);

            GameObject commandObject = CreateObject("Single Bee command");
            object command = commandObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Commands.Command"));
            RuntimeAssembly.SetField(command, "CommandType", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+CommandTypes"), "Aggressive"));
            RuntimeAssembly.SetField(command, "Level", level);
            RuntimeAssembly.SetField(command, "Stage", stage);
            RuntimeAssembly.SetField(command, "IsDead", false);
            RuntimeAssembly.Invoke(command, "SetSquad", squad);
            RuntimeAssembly.Invoke(squad, "SetCommand", command);

            Assert.DoesNotThrow(() => RuntimeAssembly.Invoke(ship, "Kill", null, null, null, true));

            Assert.That(RuntimeAssembly.GetField(ship, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetField(squad, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetField(command, "IsDead"), Is.True);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(state, "Ships")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(state, "Squads")), Is.Zero);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(state, "ShipsToRelease")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(state, "SquadsToRelease")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(state, "CommandsToRelease")), Is.EqualTo(1));

            Array hivemindBySide = (Array)RuntimeAssembly.GetField(state, "HivemindShips");
            IDictionary beeObservers = (IDictionary)hivemindBySide.GetValue(1);
            Assert.That(beeObservers.Contains(9001L), Is.False,
                "A dead one-ship Bee squad must stop contributing Hivemind vision immediately.");
        }

        private GameObject CreateObject(string name)
        {
            GameObject gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }
    }
}
