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
        public override void Setup(Level level, long id, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, id, fleetShip, squad, offsetFromCenter);

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
            Squad squad = Level.gameObject.AddComponent<Squad>();
            squad.IsImmobile = true;
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
                $"{Squad.Name} - {MinionType} Spawn #{BeaconsDropped}",
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

                int id = Utilities.GetNegativeFleetshipId();
                Beacon ship = (Beacon)Level.LevelConstructor.InstantiateShip(MinionType);


                if (ship != null)
                {
                    Squad squad = CreateMinionSquad();
                    ship.Setup(
                        Level,
                        Level.State.GetId(),
                        new FleetShip(id, $"{Name} -> {MinionType} #{Id}", MinionType, false, true, false, 0, 0, 0, 0, 0, 0, 0),
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
                        ChargingBar.gameObject.SetActive(false);
                        CanDropBeacons = false;
                    }
                    else
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
    }


}
