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
            // Create Squad
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
            if (CanDropBeacons && Time.realtimeSinceStartup - TimeSinceLastBeaconDropped > ConfigData.MinimumDelayPerBeacon)
            {
                //Debug.Log($"{Name} is dropping a beacon");
                TimeSinceLastBeaconDropped = Time.realtimeSinceStartup;

                long id = Utilities.GetNegativeFleetshipId();
                Beacon ship = (Beacon)Level.LevelConstructor.InstantiateShip(MinionType);


                if (ship != null)
                {
                    Squad squad = CreateMinionSquad();
                    ship.Setup(
                        Level,
                        new FleetShip(id, MinionType, false, true, false, 0, 0, 0, 0, 0, 0, 0),
                        squad,
                        Vector2.zero
                    );

                    ship.IsMinionShip = true;
                    ship.SetColor();
                    ship.FleetShip = FleetShip;
                    ship.transform.localPosition = GetPosition();
                    ship.MotherSquad = Squad;
                    squad.AddShip(ship);
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
            }
            //else
            //{
            //    Debug.Log($"{Name} cannot drop a beacon because not enough time ({ConfigData.MinimumDelayPerBeacon}s) has passed or it has already dropped" +
            //        $"the max number of beacons ({BeaconsDropped}/{ConfigData.MaxBeaconsDroppedPerScout})");
            //}
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
