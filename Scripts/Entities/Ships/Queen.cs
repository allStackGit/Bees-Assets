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
        public QueenExplosionAnimation QueenExplosionAnimation;

        //private int _maxMinionsPerSquad = 16;
        private ScaledTimer _spawnMinionsTimer = new ScaledTimer();
        public override void Create(Stage stage)
        {
            base.Create(stage);
            //if (MinionCount > _maxMinionsPerSquad)
            //{
            //    Debug.LogError($"Queen Property [MinionCount] (MinionCount) cannot be greater than [_maxMinionsPerSquad] ({_maxMinionsPerSquad})");
            //}
            if (!Stage.IsTraining)
            {
                QueenExplosionAnimation = ShipExplosion.GetComponentInChildren<QueenExplosionAnimation>();
                QueenExplosionAnimation.Queen = this;
                QueenExplosionAnimation.Remains = ShipRemains;
                HasRemainsShip = false;
            }

            RotationSpeed = Speed * ConfigData.Configuration.RotationMultiplier / 4;
        }
        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);
            _spawnMinionsTimer.Reuse(SpawnFrequency, SpawnMinions, true);
            Level.AddTimer(_spawnMinionsTimer);
            //InvokeRepeating(nameof(SpawnMinions), SpawnFrequency, SpawnFrequency);
        }
        public override void ClearData()
        {
            base.ClearData();
            MinionSquadsCount = 0;
            CurrentMinionSquad = null;
            MinionSquads.Clear();
        }


        private void SpawnMinions()
        {
            // Spawn the minions
            //Debug.Log($"Spawning {MinionCount} {MinionType}s at {SpawnPoint}");
            CurrentMinionSquad = null;

            for (int shipIndex = 0; shipIndex < MinionCount; shipIndex++)
            {
                StartCoroutine(SpawnMinion(shipIndex, GetPosition() + SpawnPoint));
            }

        }

        private Squad CreateMinionSquad()
        {
            // Create Squad
            Squad squad = Stage.Pool.GetSquadFromPool();
            squad.Setup(
                Level,
                Squad.SavedSquad,
                Squad.GetShootingStrategy(),
                Squad.CeaseFire,
                Squad.IsMatchingSpeed,
                Squad.ShouldChase(),
                false,
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
            long id = Utilities.GetNegativeFleetshipId();
            Vector2 offset = ConfigData.QueenYellowJacketSpawnFormation[shipIndex];

            Ship ship = Level.LevelConstructor.InstantiateShip(MinionType);

            ship.Setup(
                    Level,
                    new FleetShip(id, MinionType, false, true, false, 0, 0, 0, 0, 0, 0, 0),
                    squad,
                    offset
                );
            ship.IsMinionShip = true;
            squad.AddShip(ship);
            ship.FleetShip = FleetShip;

            Vector2 position = GetPosition();
            ship.Transform.localPosition = Utilities.RotatePointAroundPoint(position, position + SpawnPoint, Rotation * Mathf.Deg2Rad);

            if (shipIndex > 0 && squad.HasDestination)
            {
                //Debug.Log($"Moving minion {ship.Name} to gathering point: {squad.Destination + ship.OffsetFromCenter}");
                ship.MoveToPoint(squad.Destination + ship.OffsetFromCenter);
            }
            else
            {
                squad.Move(squadGatheringPoint + new Vector2(0, -10));
                //Debug.Log($"Moving minion {ship.Name} to gathering point: {squadGatheringPoint + offset + new Vector2(0, -10)}");
                ship.MoveToPoint(squadGatheringPoint + offset + new Vector2(0, -10));
            }
            ship.SetSquadName();

        }

        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            Level.CancelTimer(_spawnMinionsTimer);
            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }

    }


}
