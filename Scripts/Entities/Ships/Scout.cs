using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Scout : Ship
    {
        public ConfigData.ShipTypes MinionType = ConfigData.ShipTypes.Beacon;
        public float TimeSinceLastBeaconDropped = 0;
        public int BeaconsDropped = 0;
        public List<Squad> MinionSquads = new List<Squad>();
        public ChargingBar ChargingBar;
        public bool CanDropBeacons;

        private float BeaconClock => Stage != null && Stage.IsTraining ? Time.time : Time.realtimeSinceStartup;

        public bool IsBeaconReady =>
            CanDropBeacons &&
            BeaconsDropped < ConfigData.MaxBeaconsDroppedPerScout &&
            BeaconClock - TimeSinceLastBeaconDropped > ConfigData.MinimumDelayPerBeacon;

        public override void Create(Stage stage)
        {
            base.Create(stage);
            if (IsUserControlled)
            {
                ChargingBar.Create(this, ConfigData.MinimumDelayPerBeacon);
            }
            else
            {
                Destroy(ChargingBar.gameObject);
            }
        }

        public override void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, fleetShip, squad, offsetFromCenter);
            CanDropBeacons = true;
            // A fresh Scout starts fully charged. Training uses scaled simulation time so accelerated
            // ML-Agents workers do not wait real-world seconds for a gameplay cooldown.
            TimeSinceLastBeaconDropped = BeaconClock - ConfigData.MinimumDelayPerBeacon;
            if (IsUserControlled)
            {
                ChargingBar.Setup();
            }
        }

        public override void ClearData()
        {
            base.ClearData();
            TimeSinceLastBeaconDropped = 0;
            BeaconsDropped = 0;
            MinionSquads.Clear();
        }

        private Squad CreateMinionSquad()
        {
            Squad squad = Stage.Pool.GetSquadFromPool();
            squad.IsMinionSquad = true;
            squad.Setup(
                Level,
                Squad.SavedSquad,
                Squad.GetShootingStrategy(),
                Squad.CeaseFire,
                Squad.IsMatchingSpeed,
                Squad.ShouldChase(),
                true,
                Utilities.GetNegativeSavedSquadId(),
                Squad.Side,
                Level.State.OriginalSquadCounts[Side - 1] + 1,
                $"{Name} - {MinionType} Spawn #{BeaconsDropped}",
                Squad.Color
            );
            Level.State.AddSquad(squad);
            MinionSquads.Add(squad);
            BeaconsDropped++;
            return squad;
        }

        public void DropBeacon()
        {
            if (!IsBeaconReady)
            {
                return;
            }

            TimeSinceLastBeaconDropped = BeaconClock;
            long id = Utilities.GetNegativeFleetshipId();
            Beacon ship = (Beacon)Level.LevelConstructor.InstantiateShip(MinionType);
            if (ship == null)
            {
                return;
            }

            Squad squad = CreateMinionSquad();
            ship.IsMinionShip = true;
            ship.Setup(
                Level,
                new FleetShip(id, MinionType, false, false, 0, 0, 0, 0, 0, 0, 0),
                squad,
                Vector2.zero
            );

            ship.SetColor();
            ship.FleetShip = FleetShip;
            ship.transform.localPosition = GetPosition();
            ship.MotherSquad = Squad;
            squad.AddShip(ship);
            squad.CanAcceptUserInput = false;
            ship.LookForShips();

            if (BeaconsDropped == ConfigData.MaxBeaconsDroppedPerScout)
            {
                if (IsUserControlled)
                {
                    ChargingBar.gameObject.SetActive(false);
                }
                CanDropBeacons = false;
            }
            else if (IsUserControlled)
            {
                ChargingBar.DrainBar();
            }
        }

        public override void Deactivate()
        {
            base.Deactivate();
            if (IsUserControlled)
            {
                ChargingBar.gameObject.SetActive(false);
            }
        }

        public override void Activate()
        {
            base.Activate();
            if (IsUserControlled)
            {
                ChargingBar.gameObject.SetActive(true);
            }
        }
    }
}
