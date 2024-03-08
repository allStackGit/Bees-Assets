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
using UnityEngine.Assertions;

namespace Assets.Scripts.Entities.Ships
{
    public class Queen : Ship
    {
        public string MinionType;
        public int MinionCount;
        public Vector2 SpawnPoint;
        public int SpawnFrequency;
        public int TimeBetweenMinions;
        public int MinionSquadsCount = 0;
        public Squad CurrentMinionSquad;
        public List<Squad> MinionSquads = new List<Squad>();

        private int _maxMinionsPerSquad = 16;

        private void Start()
        {
            if (MinionCount > _maxMinionsPerSquad)
            {
                Debugger.Exception($"Queen Property [MinionCount] (MinionCount) cannot be greater than [_maxMinionsPerSquad] ({_maxMinionsPerSquad})");
            }
            InvokeRepeating(nameof(SpawnMinions), SpawnFrequency, SpawnFrequency);
        }

        private void SpawnMinions()
        {
            // Spawn the minions
            //Debug.Log($"Spawning {MinionCount} {MinionType}s at {SpawnPoint}");
            for (int shipIndex = 0; shipIndex < MinionCount; shipIndex++)
            {
                StartCoroutine(SpawnMinion(shipIndex, GetPosition() + SpawnPoint));
            }

        }

        private Squad CreateMinionSquad()
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
                $"{Squad.Name} - {MinionType} Spawn #{MinionSquadsCount}",
                Squad.Color
            );
            state.AddSquad(squad);
            CurrentMinionSquad = squad;
            MinionSquads.Add(squad);
            squad.SavedSquad = Squad.SavedSquad;
            MinionSquadsCount++;
            return squad;
        }
        private IEnumerator SpawnMinion(int shipIndex, Vector2 squadGatheringPoint)
        {
            yield return new WaitForSeconds(shipIndex * TimeBetweenMinions);

            //Debug.Log($"Spawning minion {MinionType} #{shipIndex}");
            Squad squad = CurrentMinionSquad;
            if (squad == null || squad.IsDead)
            {
                squad = CreateMinionSquad();
            }
            int id = (int)Utilities.Hash() + ConfigData.AllShips.GetFleetShips().Count;
            Vector2 offset = ConfigData.QueenYellowJacketSpawnFormation[shipIndex];

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
                    offset
                );
                ship.IsMinionShip = true;
            }
            squad.AddShip(ship);
            ship.SetColor();

            Vector2 position = GetPosition();
            Vector2 rotatedSpawnPosition = Utilities.RotatePointAroundPoint(position, position + SpawnPoint, GetRotation() * Mathf.Deg2Rad);
            ship.transform.localPosition = rotatedSpawnPosition;

            ship.MoveToPoint(squadGatheringPoint + offset + new Vector2(0, -10));
            ship.SetSquadName();

        }
    }


}
