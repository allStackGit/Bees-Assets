using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using System;
using System.Collections;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Assertions;

namespace Assets.Scripts.Entities.Ships
{
    public class Queen : Ship
    {
        public ConfigData.ShipTypes MinionType = ConfigData.ShipTypes.YellowJacket;
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
                Debug.LogError($"Queen Property [MinionCount] (MinionCount) cannot be greater than [_maxMinionsPerSquad] ({_maxMinionsPerSquad})");
            }
            InvokeRepeating(nameof(SpawnMinions), SpawnFrequency, SpawnFrequency);
            RotationSpeed = Speed * ConfigData.Configuration.RotationMultiplier / 4;
        }

        private void SpawnMinions()
        {
            // Spawn the minions
            //Debug.Log($"Spawning {MinionCount} {MinionType}s at {SpawnPoint}");
            CurrentMinionSquad = null;

            for (int shipIndex = 0; shipIndex < MinionCount && !IsDead; shipIndex++)
            {
                StartCoroutine(SpawnMinion(shipIndex, GetPosition() + SpawnPoint));
            }

        }

        private Squad CreateMinionSquad()
        {
            // Create Squad
            Squad squad = Level.gameObject.AddComponent<Squad>();
            squad.Setup(
                Level,
                Squad.SavedSquad,
                Squad.GetShootingStrategy(),
                Squad.CeaseFire,
                Squad.IsMatchingSpeed,
                Squad.ShouldChase(),
                Utilities.GetNegativeSavedSquadId(),
                Squad.Side,
                Level.State.OriginalSquadCounts[Side - 1] + 1,
                $"{Squad.Name} - {MinionType} Spawn #{MinionSquadsCount}",
                Squad.Color
            );
            Level.State.AddSquad(squad);
            CurrentMinionSquad = squad;
            MinionSquads.Add(squad);
            MinionSquadsCount++;
            squad.IsGrowingSquad = true;
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
                squad.SetSquadTab();
            }
            int id = Utilities.GetNegativeFleetshipId();
            Vector2 offset = ConfigData.QueenYellowJacketSpawnFormation[shipIndex];

            Ship ship = Level.LevelConstructor.InstantiateShip(MinionType);

            if (ship != null)
            {
                ship.Setup(
                    Level,
                    Level.State.GetId(),
                    new FleetShip(id, $"{ShipType} Minion {MinionType} - #{id}", MinionType, false, true, false, 0, 0, 0, 0, 0, 0, 0),
                    squad,
                    offset
                );
                ship.IsMinionShip = true;
            }
            squad.AddShip(ship);
            ship.FleetShip = FleetShip;

            Vector2 position = GetPosition();
            Vector2 rotatedSpawnPosition = Utilities.RotatePointAroundPoint(position, position + SpawnPoint, GetRotation() * Mathf.Deg2Rad);
            ship.transform.localPosition = rotatedSpawnPosition;

            if (shipIndex > 0 && squad.HasDestination)
            {
                ship.MoveToPoint(squad.Destination + ship.OffsetFromCenter);
            }
            else
            {
                ship.MoveToPoint(squadGatheringPoint + offset + new Vector2(0, -10));
            }
            ship.SetSquadName();

        }
    }


}
