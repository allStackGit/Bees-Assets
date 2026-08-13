using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Levels
{
    public partial class GameState
    {
        public void AddSpottedShips(List<Ship> spottedShips, Ship spotter)
        {
            List<SpottedShip> known = SpottedShips[spotter.Side - 1];
            for (int i = 0; i < spottedShips.Count; i++)
            {
                Ship spotted = spottedShips[i];
                bool alreadyKnown = false;
                for (int j = 0; j < known.Count; j++)
                {
                    if (known[j].Ship.Id == spotted.Id)
                    {
                        alreadyKnown = true;
                        break;
                    }
                }
                if (!alreadyKnown)
                {
                    known.Add(new SpottedShip(spotted, spotter.Id));
                }
            }
        }

        public ShipDamageStatus GetShipDamageStatus(int side, Ship potentialTargetShip)
        {
            int sideIndex = side - 1;
            if (!ShipDamageStatusesById[sideIndex].TryGetValue(potentialTargetShip.Id, out ShipDamageStatus status) ||
                status.Ship != potentialTargetShip)
            {
                status = new ShipDamageStatus(potentialTargetShip);
                ShipDamageStatuses[sideIndex].Add(status);
                ShipDamageStatusesById[sideIndex][potentialTargetShip.Id] = status;
            }
            return status;
        }

        public List<Obstacle> GetObstacles()
        {
            return Obstacles;
        }

        public List<Ship> GetShips(int side = 0)
        {
            return side switch
            {
                1 => Ships.Where(ship => ship.Side == 1).ToList(),
                2 => Ships.Where(ship => ship.Side == 2).ToList(),
                _ => Ships
            };
        }

        public bool RecordHiveMindSighting(Ship observer, Ship spotted)
        {
            if (observer == null || spotted == null || observer.IsDead || spotted.IsDead || observer.Side == spotted.Side)
            {
                return false;
            }

            int sideIndex = observer.Side - 1;
            if (sideIndex < 0 || sideIndex >= HivemindShips.Length ||
                !HivemindShips[sideIndex].TryGetValue(observer.Id, out HashSet<Ship> observerVisibility))
            {
                return false;
            }

            observerVisibility.Add(spotted);
            return VisionCache[sideIndex].Add(spotted);
        }

        public HashSet<Ship> GetShipsVisibleToHiveMind(int side)
        {
            return VisionCache[side - 1];
        }

        public HashSet<ConfigData.ShipTypes> GetHumanShipTypes()
        {
            return GetShipTypes(ConfigData.Configuration.HumanSide);
        }

        public List<Ship> GetBeeShips()
        {
            return GetShips(ConfigData.Configuration.BeeSide);
        }

        public HashSet<ConfigData.ShipTypes> GetBeeShipTypes()
        {
            return GetShipTypes(ConfigData.Configuration.BeeSide);
        }

        private HashSet<ConfigData.ShipTypes> GetShipTypes(int side)
        {
            HashSet<ConfigData.ShipTypes> types = new HashSet<ConfigData.ShipTypes>();
            for (int i = 0; i < Ships.Count; i++)
            {
                Ship ship = Ships[i];
                if (ship.Side == side)
                {
                    types.Add(ship.ShipType);
                }
            }
            return types;
        }

        public int GetTsvBySide(int side)
        {
            int total = 0;
            for (int i = 0; i < Squads.Count; i++)
            {
                Squad squad = Squads[i];
                if (squad.Side == side && !squad.IsDead)
                {
                    total += squad.Tsv;
                }
            }
            return total;
        }

        public List<Ship> GetHumanShips()
        {
            return GetShips(ConfigData.Configuration.HumanSide);
        }

        public List<Ship> GetAllEnemyShips(int side)
        {
            return Ships.Where(ship => ship.Side != side).ToList();
        }

        public Ship GetShipById(long id)
        {
            return ShipsById.GetValueOrDefault(id);
        }

        public List<Squad> GetSquadsVisibleToHiveMind(int side = 0)
        {
            if (side == ConfigData.Configuration.UserSide && Level.HasPlayer)
            {
                return GetEnemySquads(side);
            }

            return GetShipsVisibleToHiveMind(side)
                .Select(ship => ship.Squad)
                .Where(squad => squad != null && !squad.IsDead)
                .Distinct()
                .ToList();
        }

        public Squad GetSquadByNumber(int side, int squadNumber)
        {
            for (int i = 0; i < Squads.Count; i++)
            {
                Squad squad = Squads[i];
                if (!squad.IsDead && squad.Side == side && squad.SquadNumber == squadNumber)
                {
                    return squad;
                }
            }
            return null;
        }

        public Squad GetSquadById(long id)
        {
            for (int i = 0; i < Squads.Count; i++)
            {
                Squad squad = Squads[i];
                if (squad.Id == id)
                {
                    return squad;
                }
            }
            return null;
        }

        public List<Squad> GetAllSquads()
        {
            return Squads;
        }

        public List<Squad> GetSquadsBySide(int side)
        {
            return GetAllSquads().Where(squad => squad.Side == side && !squad.IsDead).ToList();
        }

        public List<Squad> GetEnemySquads(int side)
        {
            return GetAllSquads().Where(squad => squad.Side != side && !squad.IsDead).ToList();
        }

        public bool IsSideKilled(int side)
        {
            if (TryGetCapturedEliminationState(side, out bool capturedState))
            {
                return capturedState;
            }

            bool hasShip = false;
            for (int i = 0; i < Ships.Count; i++)
            {
                Ship ship = Ships[i];
                if (ship.Side != side)
                {
                    continue;
                }
                hasShip = true;
                if (ship.IsMobile)
                {
                    return false;
                }
            }
            return !hasShip || true;
        }
    }
}
