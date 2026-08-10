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
            foreach (Ship spotted in spottedShips)
            {
                // Existing semantics are ship-level de-duplication for a side; spotter ID is
                // retained as attribution on the first sighting.
                if (!known.Any(existing => existing.Ship.Id == spotted.Id))
                {
                    known.Add(new SpottedShip(spotted, spotter.Id));
                }
            }
        }

        public ShipDamageStatus GetShipDamageStatus(int side, Ship potentialTargetShip)
        {
            ShipDamageStatus status = ShipDamageStatuses[side - 1]
                .FirstOrDefault(entry => entry.Ship == potentialTargetShip);

            if (status == null)
            {
                status = new ShipDamageStatus(potentialTargetShip);
                ShipDamageStatuses[side - 1].Add(status);
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

        public HashSet<Ship> GetShipsVisibleToHiveMind(int side)
        {
            VisionCache[side - 1] = HivemindShips[side - 1].Aggregate(
                new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance),
                (sum, dictionary) =>
                {
                    sum.UnionWith(dictionary.Value.Where(ship => !ship.IsDead));
                    return sum;
                });
            return VisionCache[side - 1];
        }

        public HashSet<ConfigData.ShipTypes> GetHumanShipTypes()
        {
            return GetHumanShips().Select(ship => ship.ShipType).ToHashSet();
        }

        public List<Ship> GetBeeShips()
        {
            return GetShips(ConfigData.Configuration.BeeSide);
        }

        public HashSet<ConfigData.ShipTypes> GetBeeShipTypes()
        {
            return GetBeeShips().Select(ship => ship.ShipType).ToHashSet();
        }

        public int GetTsvBySide(int side)
        {
            return GetSquadsBySide(side).Sum(squad => squad.Tsv);
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

            // Visibility is accumulated per ship, but matchup selection is per squad.
            // Without Distinct(), larger visible squads are duplicated in the matchup queue
            // and receive disproportionate weight, especially for Random strategy selection.
            return GetShipsVisibleToHiveMind(side)
                .Select(ship => ship.Squad)
                .Where(squad => squad != null && !squad.IsDead)
                .Distinct()
                .ToList();
        }

        public Squad GetSquadByNumber(int side, int squadNumber)
        {
            return GetSquadsBySide(side).FirstOrDefault(squad => squad.SquadNumber == squadNumber);
        }

        public Squad GetSquadById(long id)
        {
            return GetAllSquads().FirstOrDefault(squad => squad.Id == id);
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
            List<Ship> sideShips = GetShips(side);
            return sideShips.Count == 0 || !sideShips.Any(ship => ship.IsMobile);
        }
    }
}