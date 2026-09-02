using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using UnityEngine;

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
            int sideIndex = side - 1;
            return sideIndex >= 0 && sideIndex < ShipsBySide.Length
                ? ShipsBySide[sideIndex]
                : Ships;
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

        /// <summary>
        /// Adds a mining asteroid to the side-wide Hive Mind memory. Unlike enemy-ship visibility,
        /// strategic map-object knowledge is intentionally persistent after the observer moves away;
        /// the object is forgotten only when it leaves this Level/lifecycle.
        /// </summary>
        public bool RecordHiveMindMiningAsteroidSighting(Ship observer, MiningAsteroid asteroid)
        {
            if (observer == null || asteroid == null || observer.IsDead || asteroid.IsDead || !observer.IsHiveMindControlled)
            {
                return false;
            }

            int sideIndex = observer.Side - 1;
            if (sideIndex < 0 || sideIndex >= HiveMindMiningAsteroidCache.Length)
            {
                return false;
            }
            if (Level != null && (observer.Level != Level || asteroid.Level != Level))
            {
                return false;
            }

            return HiveMindMiningAsteroidCache[sideIndex].Add(asteroid);
        }

        /// <summary>
        /// Refreshes strategic-object discovery from every live Hive Mind observer on a side.
        /// This deliberately uses the same per-ship sight radius as HiveMindVision rather than
        /// exposing the omniscient MiningAsteroids registry to a policy. The per-frame guard makes
        /// the shared computation independent of how many agents request observations that frame.
        /// </summary>
        public void RefreshHiveMindMapObjectVision(int side)
        {
            int sideIndex = side - 1;
            if (sideIndex < 0 || sideIndex >= HiveMindMiningAsteroidCache.Length)
            {
                return;
            }
            if (HiveMindMapObjectRefreshFrame[sideIndex] == Time.frameCount)
            {
                return;
            }
            HiveMindMapObjectRefreshFrame[sideIndex] = Time.frameCount;

            if (MiningAsteroids.Count == 0)
            {
                return;
            }

            List<Ship> observers = ShipsBySide[sideIndex];
            for (int observerIndex = 0; observerIndex < observers.Count; observerIndex++)
            {
                Ship observer = observers[observerIndex];
                if (observer == null || observer.IsDead || !observer.IsHiveMindControlled || observer.HiveMindVision == null)
                {
                    continue;
                }

                float visionRange = observer.HiveMindVision.Collider != null
                    ? observer.HiveMindVision.Collider.radius
                    : (observer.Sight > 0 ? observer.Sight : observer.MaxRange);
                if (visionRange <= 0f)
                {
                    continue;
                }

                Vector2 observerPosition = observer.GetPosition();
                float visionRangeSquared = visionRange * visionRange;
                foreach (MiningAsteroid asteroid in MiningAsteroids)
                {
                    if (asteroid == null || asteroid.IsDead || asteroid.Level != observer.Level)
                    {
                        continue;
                    }
                    Vector2 relative = (Vector2)asteroid.transform.localPosition - observerPosition;
                    if (relative.sqrMagnitude <= visionRangeSquared)
                    {
                        RecordHiveMindMiningAsteroidSighting(observer, asteroid);
                    }
                }
            }
        }

        public HashSet<MiningAsteroid> GetMiningAsteroidsVisibleToHiveMind(int side)
        {
            RefreshHiveMindMapObjectVision(side);
            return HiveMindMiningAsteroidCache[side - 1];
        }

        public void ForgetHiveMindMiningAsteroid(MiningAsteroid asteroid)
        {
            if (asteroid == null)
            {
                return;
            }
            for (int sideIndex = 0; sideIndex < HiveMindMiningAsteroidCache.Length; sideIndex++)
            {
                HiveMindMiningAsteroidCache[sideIndex]?.Remove(asteroid);
            }
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
            List<Ship> sideShips = GetShips(side);
            for (int i = 0; i < sideShips.Count; i++)
            {
                types.Add(sideShips[i].ShipType);
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

            List<Ship> sideShips = GetShips(side);
            for (int i = 0; i < sideShips.Count; i++)
            {
                if (sideShips[i].IsMobile)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
