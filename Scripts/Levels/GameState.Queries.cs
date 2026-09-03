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
            bool isFirstSideWideSighting = VisionCache[sideIndex].Add(spotted);
            if (isFirstSideWideSighting)
            {
                global::RlOneVsOneEpisodeCoordinator.RecordShipDiscovery(observer, spotted);
            }
            return isFirstSideWideSighting;
        }

        public HashSet<Ship> GetShipsVisibleToHiveMind(int side)
        {
            RefreshHiveMindMapObjectVision(side);
            return VisionCache[side - 1];
        }

        public bool RecordHiveMindObstacleSighting(Ship observer, Obstacle obstacle)
        {
            if (observer == null || obstacle == null || observer.IsDead || obstacle.IsDead ||
                !observer.IsHiveMindControlled)
            {
                return false;
            }

            int sideIndex = observer.Side - 1;
            if (sideIndex < 0 || sideIndex >= HiveMindObstacleCache.Length)
            {
                return false;
            }
            if (Level != null && (observer.Level != Level || obstacle.Level != Level))
            {
                return false;
            }

            bool isNew = HiveMindObstacleCache[sideIndex].Add(obstacle);
            if (obstacle is MiningAsteroid miningAsteroid)
            {
                bool isNewMiningAsteroid = HiveMindMiningAsteroidCache[sideIndex].Add(miningAsteroid);
                if (isNewMiningAsteroid)
                {
                    global::RlOneVsOneEpisodeCoordinator.RecordMiningAsteroidDiscovery(observer, miningAsteroid);
                }
            }
            else if (isNew)
            {
                global::RlOneVsOneEpisodeCoordinator.RecordObstacleDiscovery(observer, obstacle);
            }
            return isNew;
        }

        public bool RecordHiveMindMapObjectSighting(Ship observer, MapObject mapObject)
        {
            if (observer == null || mapObject == null || observer.IsDead || mapObject.IsDead ||
                !observer.IsHiveMindControlled)
            {
                return false;
            }

            int sideIndex = observer.Side - 1;
            if (sideIndex < 0 || sideIndex >= HiveMindMapObjectCache.Length)
            {
                return false;
            }
            if (Level != null && (observer.Level != Level || mapObject.Level != Level))
            {
                return false;
            }

            bool isNew = HiveMindMapObjectCache[sideIndex].Add(mapObject);
            if (isNew)
            {
                global::RlOneVsOneEpisodeCoordinator.RecordMapObjectDiscovery(observer, mapObject);
            }
            return isNew;
        }

        /// <summary>
        /// Adds a mining asteroid to the side-wide Hive Mind memory. Strategic/environment knowledge
        /// remains shared after the observer moves away and is forgotten only when the object leaves
        /// this Level lifecycle.
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

            bool isNew = HiveMindMiningAsteroidCache[sideIndex].Add(asteroid);
            HiveMindObstacleCache[sideIndex].Add(asteroid);
            if (isNew)
            {
                global::RlOneVsOneEpisodeCoordinator.RecordMiningAsteroidDiscovery(observer, asteroid);
            }
            return isNew;
        }

        /// <summary>
        /// Refreshes side-wide RL/Hive Mind perception from every live observer. Trigger callbacks are
        /// still the cheap normal path for ships/mining asteroids; this geometric pass makes perception
        /// complete and independent of physics-layer filtering for large walls, moving asteroids,
        /// HumanTarget-style ships and targetable MapObjects such as Fire Tanks.
        /// </summary>
        public void RefreshHiveMindMapObjectVision(int side)
        {
            int sideIndex = side - 1;
            if (sideIndex < 0 || sideIndex >= HiveMindMiningAsteroidCache.Length || Level == null || Level.Map == null)
            {
                return;
            }
            if (HiveMindMapObjectRefreshFrame[sideIndex] == Time.frameCount)
            {
                return;
            }
            HiveMindMapObjectRefreshFrame[sideIndex] = Time.frameCount;

            List<Ship> observers = ShipsBySide[sideIndex];
            if (observers.Count == 0)
            {
                return;
            }

            GameObject[] activeObstacleObjects = null;
            MapObject[] activeMapObjects = null;
            for (int observerIndex = 0; observerIndex < observers.Count; observerIndex++)
            {
                Ship observer = observers[observerIndex];
                if (observer == null || observer.IsDead || !observer.IsHiveMindControlled ||
                    observer.HiveMindVision == null || !observer.HiveMindVision.enabled)
                {
                    continue;
                }

                // Enemy ships, including immobile target ships such as HumanTarget, use the same
                // sight mechanic as ordinary combat ships.
                for (int shipIndex = 0; shipIndex < Ships.Count; shipIndex++)
                {
                    Ship spotted = Ships[shipIndex];
                    if (spotted == null || spotted.IsDead || spotted.Side == observer.Side ||
                        spotted.Level != observer.Level)
                    {
                        continue;
                    }
                    if (observer.HiveMindVision.CanSee(spotted.Collider, spotted.GetPosition()))
                    {
                        RecordHiveMindSighting(observer, spotted);
                    }
                }

                if (activeObstacleObjects == null)
                {
                    activeObstacleObjects = PathfinderObstacleScope.GetActiveObstacleObjects(Level);
                }
                for (int obstacleIndex = 0; obstacleIndex < activeObstacleObjects.Length; obstacleIndex++)
                {
                    Obstacle obstacle = activeObstacleObjects[obstacleIndex].GetComponent<Obstacle>();
                    if (obstacle == null || obstacle.IsDead || obstacle.Level != observer.Level)
                    {
                        continue;
                    }
                    Collider2D sightCollider = obstacle.ClearanceMappingCollider != null
                        ? obstacle.ClearanceMappingCollider
                        : obstacle.Collider;
                    if (observer.HiveMindVision.CanSee(sightCollider, obstacle.GetPosition()))
                    {
                        RecordHiveMindObstacleSighting(observer, obstacle);
                    }
                }

                if (activeMapObjects == null)
                {
                    activeMapObjects = Level.Map.Transform.GetComponentsInChildren<MapObject>(false);
                }
                for (int objectIndex = 0; objectIndex < activeMapObjects.Length; objectIndex++)
                {
                    MapObject mapObject = activeMapObjects[objectIndex];
                    if (mapObject == null || mapObject.IsDead || mapObject.Level != observer.Level)
                    {
                        continue;
                    }
                    Vector2 position = mapObject.transform.localPosition;
                    if (observer.HiveMindVision.CanSee(mapObject.Collider, position))
                    {
                        RecordHiveMindMapObjectSighting(observer, mapObject);
                    }
                }
            }
        }

        public HashSet<MiningAsteroid> GetMiningAsteroidsVisibleToHiveMind(int side)
        {
            RefreshHiveMindMapObjectVision(side);
            return HiveMindMiningAsteroidCache[side - 1];
        }

        public HashSet<Obstacle> GetObstaclesVisibleToHiveMind(int side)
        {
            RefreshHiveMindMapObjectVision(side);
            return HiveMindObstacleCache[side - 1];
        }

        public HashSet<MapObject> GetMapObjectsVisibleToHiveMind(int side)
        {
            RefreshHiveMindMapObjectVision(side);
            return HiveMindMapObjectCache[side - 1];
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
                HiveMindObstacleCache[sideIndex]?.Remove(asteroid);
            }
        }

        public void ForgetHiveMindObstacle(Obstacle obstacle)
        {
            if (obstacle == null)
            {
                return;
            }
            for (int sideIndex = 0; sideIndex < HiveMindObstacleCache.Length; sideIndex++)
            {
                HiveMindObstacleCache[sideIndex]?.Remove(obstacle);
                if (obstacle is MiningAsteroid miningAsteroid)
                {
                    HiveMindMiningAsteroidCache[sideIndex]?.Remove(miningAsteroid);
                }
            }
        }

        public void ForgetHiveMindMapObject(MapObject mapObject)
        {
            if (mapObject == null)
            {
                return;
            }
            for (int sideIndex = 0; sideIndex < HiveMindMapObjectCache.Length; sideIndex++)
            {
                HiveMindMapObjectCache[sideIndex]?.Remove(mapObject);
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
                if (Squads[i].Id == id)
                {
                    return Squads[i];
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
