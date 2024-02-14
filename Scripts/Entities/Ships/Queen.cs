using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Level;
using Assets.Scripts.Level.Commands;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Queen : Ship
    {
        public string MinionType;
        public int MinionCount;
        public Vector2 SpawnPoint;
        public int SpawnFrequency;
        public int TimeBetweenMinions;
        public Squad CurrentMinionSquad;
        public List<Squad> MinionSquads = new List<Squad>();

        private void Start()
        {
            InvokeRepeating(nameof(SpawnMinions), SpawnFrequency, SpawnFrequency);
        }

        private void SpawnMinions()
        {
            GameState state = Level.GetState();
            // Create Squad
            Squad squad = Level.gameObject.AddComponent<Squad>();
            squad.Setup(
                Level,
                null,
                Squad.GetShootingStrategy(),
                Squad.CeaseFire,
                Squad.IsMatchingSpeed,
                (int)Utilities.Hash() + ConfigData.AllShips.GetSavedSquads().Count,
                Squad.Side,
                state.GetSquadsBySide(Side).Count + 1,
                $"{Squad.Name} - {MinionType} Spawn",
                Squad.Color
            );
            state.AddSquad(squad);


            // Spawn the minions
            Debugger.Log($"Spawning {MinionCount} {MinionType}s at {SpawnPoint}");
            for (int shipIndex = 0; shipIndex  < MinionCount; shipIndex++)
            {
                StartCoroutine(SpawnMinion(shipIndex));
                this.Invoke(() => SpawnMinion(), shipIndex*TimeBetweenMinions);
            }

        }

        private void SpawnMinion(int shipIndex, Squad squad)
        {
            Debugger.Log($"Spawning minion {MinionType}");

            int id = (int)Utilities.Hash() + ConfigData.AllShips.GetFleetShips().Count;
            Vector2 firstPosition = ConfigData.CarrierColumnFormationOffsets[shipIndex];

            Ship ship;
            (GameObject, Ship) tuple = Level.LevelConstructor.InstantiateShip(MinionType);
            ship = tuple.Item2;


            if (ship != null)
            {
                ship.Setup(
                    Level,
                    Level.GetState().EntityCount++,
                    new FleetShip(id, Side, $"{ShipType} Minion {MinionType} - #{id}", MinionType, true, false, 0, 0, 0, 0, 0, 0),
                    squad,
                    SpawnPoint
                );
            }
            squad.AddShip(ship);
            ship.SetColor();

            // To-do: Need to be able to spawn minions on top of ship and intervals and then need to be able to send them to their starting position.
            // Squad needs to start acting as a squad *after* all the minions are assembled
        }
    }


}
