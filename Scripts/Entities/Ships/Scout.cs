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
        public string MinionType = "Beacon";
        public float TimeSinceLastBeaconDropped = 0;
        public int BeaconsDropped = 0;
        public List<Squad> MinionSquads = new List<Squad>();
        public ChargingBar ChargingBar;
        public bool CanDropBeacons;

        public override void Setup(Level level, long id, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            base.Setup(level, id, fleetShip, squad, offsetFromCenter);
            if (IsUserControlled)
            {
                ChargingBar.Setup(this, ConfigData.MinimumDelayPerBeacon); // [efficiency] Make this be created instead of starting with every barge, in case it's unneeded
            }
            CanDropBeacons = true;
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
                Beacon ship;
                (GameObject, Beacon) tuple = ((GameObject, Beacon))Level.LevelConstructor.InstantiateShip("Beacon");
                ship = tuple.Item2;


                if (ship != null)
                {
                    Squad squad = CreateMinionSquad();
                    ship.Setup(
                        Level,
                        Level.State.GetId(),
                        new FleetShip(id, $"{Name} -> Beacon #{Id}", "Beacon", false, true, false, 0, 0, 0, 0, 0, 0, 0),
                        squad,
                        Vector2.zero
                    );

                    ship.IsMinionShip = true;
                    ship.SetColor();
                    ship.FleetShip = FleetShip;
                    ship.transform.position = GetPosition();
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
