using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level;
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

        private Squad CreateMinionSquad()
        {
            GameState state = Level.GetState();
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
                state.OriginalSquadCounts[Side - 1] + 1,
                $"{Squad.Name} - {MinionType} Spawn #{BeaconsDropped}",
                Squad.Color
            );
            state.AddSquad(squad);
            MinionSquads.Add(squad);
            BeaconsDropped++;
            return squad;
        }
        public void DropBeacon()
        {
            if (BeaconsDropped < ConfigData.MaxBeaconsDroppedPerScout && Time.realtimeSinceStartup - TimeSinceLastBeaconDropped > ConfigData.MinimumDelayPerBeacon)
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
                        Level.GetState().GetId(),
                        new FleetShip(id, Side, $"{Name} -> Beacon #{Id}", "Beacon", false, true, false, 0, 0, 0, 0, 0, 0, 0),
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
