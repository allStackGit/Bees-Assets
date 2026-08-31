using System.Collections.Generic;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;

namespace Assets.Scripts.Levels
{
    public partial class GameState
    {
        private readonly List<Projectile> _resetProjectiles = new List<Projectile>();
        private readonly List<Obstacle> _resetObstacles = new List<Obstacle>();
        private readonly List<Ship> _readyShips = new List<Ship>();
        private readonly HashSet<Ship> _readyShipSet = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        private readonly List<Projectile> _projectilesToRemove = new List<Projectile>();
        private readonly List<ServerRequest> _standingRequestsToRemove = new List<ServerRequest>();
        private readonly List<Command> _releaseCommands = new List<Command>();
        private readonly List<Squad> _releaseSquads = new List<Squad>();
        private readonly List<CollisionAsteroid> _releaseAsteroids = new List<CollisionAsteroid>();
        private readonly List<AsteroidPiece> _releaseAsteroidPieces = new List<AsteroidPiece>();
        private readonly List<MiningAsteroid> _releaseMiningAsteroids = new List<MiningAsteroid>();

        public bool CanShipsKeepMining()
        {
            return MiningShips.Count > 0 && MiningAsteroids.Count > 0;
        }

        public int GetId()
        {
            return Stage.Pool.ItemCount++;
        }

        public void AddShip(Ship ship)
        {
            if (!ship.IsMinionShip && !ship.IsCarrierShip)
            {
                ship.FleetShip.IsLoadedIntoLevel = true;
            }
            Ships.Add(ship);
            int sideIndex = ship.Side - 1;
            if (sideIndex >= 0 && sideIndex < ShipsBySide.Length)
            {
                ShipsBySide[sideIndex].Add(ship);
            }
            ShipsById.Add(ship.Id, ship);
            if (ship.IsHiveMindControlled)
            {
                HivemindShips[ship.Side - 1][ship.Id] =
                    new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
            }
        }

        public void AddSquad(Squad squad)
        {
            if (squad.IsMinionSquad)
            {
                int maximumSquadNumber = 0;
                for (int i = 0; i < Squads.Count; i++)
                {
                    Squad existing = Squads[i];
                    if (existing.Side == squad.Side && existing.SquadNumber > maximumSquadNumber)
                    {
                        maximumSquadNumber = existing.SquadNumber;
                    }
                }
                squad.SquadNumber = maximumSquadNumber + 1;
            }
            else
            {
                squad.SavedSquad.IsLoadedIntoLevel = true;
                OriginalSquadCounts[squad.Side - 1]++;
            }
            Squads.Add(squad);
        }

        public void RemoveSquad(Squad squad)
        {
            if (!squad.IsMinionSquad)
            {
                squad.SavedSquad.IsLoadedIntoLevel = false;
            }

            if (Level != null && Level.IsLevelSetupOnServer)
            {
                int removedSquadItemId = squad.ItemId;
                _standingRequestsToRemove.Clear();
                foreach (ServerRequest request in ConfigData.Socket.StandingRequests)
                {
                    if ((request is CommandRequest commandRequest &&
                         ReferenceEquals(commandRequest.Squad, squad) &&
                         commandRequest.SquadId == removedSquadItemId) ||
                        (request is MatchupStrategyRequest matchupRequest &&
                         ReferenceEquals(matchupRequest.Squad, squad) &&
                         matchupRequest.SquadId == removedSquadItemId))
                    {
                        _standingRequestsToRemove.Add(request);
                    }
                }
                for (int requestIndex = 0; requestIndex < _standingRequestsToRemove.Count; requestIndex++)
                {
                    ConfigData.Socket.StandingRequests.Remove(_standingRequestsToRemove[requestIndex]);
                }
            }

            squad.IsMinionSquad = false;
            Squads.Remove(squad);
            SquadsToRelease.Add(squad);
        }

        public void AddProjectile(Projectile projectile)
        {
            Projectiles.Add(projectile);
        }

        public void RemoveProjectile(Projectile projectile)
        {
            Projectiles.Remove(projectile);
        }

        public void AddObstacle(Obstacle obstacle)
        {
            Obstacles.Add(obstacle);
        }

        public void RemoveObstacle(Obstacle obstacle)
        {
            Obstacles.Remove(obstacle);
        }

        public void RemoveShip(Ship ship)
        {
            if (ship.Squad != null && ship.Squad.GetCommand() is Heal healCommand)
            {
                healCommand.ShipBecameUnavailable(ship);
            }

            for (int sideIndex = 0; sideIndex < ShipDamageStatuses.Length; sideIndex++)
            {
                List<ShipDamageStatus> statuses = ShipDamageStatuses[sideIndex];
                for (int statusIndex = statuses.Count - 1; statusIndex >= 0; statusIndex--)
                {
                    ShipDamageStatus status = statuses[statusIndex];
                    if (status == null || status.Ship == null || status.Ship == ship)
                    {
                        statuses.RemoveAt(statusIndex);
                    }
                }
                ShipDamageStatusesById[sideIndex].Remove(ship.Id);
            }
            for (int sideIndex = 0; sideIndex < SpottedShips.Length; sideIndex++)
            {
                List<SpottedShip> spotted = SpottedShips[sideIndex];
                if (spotted == null)
                {
                    continue;
                }
                for (int spottedIndex = spotted.Count - 1; spottedIndex >= 0; spottedIndex--)
                {
                    SpottedShip entry = spotted[spottedIndex];
                    if (entry == null || entry.Ship == null || entry.Ship == ship)
                    {
                        spotted.RemoveAt(spottedIndex);
                    }
                }
            }

            foreach (Dictionary<long, HashSet<Ship>> observerMap in HivemindShips)
            {
                if (observerMap == null)
                {
                    continue;
                }
                observerMap.Remove(ship.Id);
                foreach (HashSet<Ship> visibleShips in observerMap.Values)
                {
                    visibleShips?.Remove(ship);
                }
            }
            foreach (HashSet<Ship> visibleCache in VisionCache)
            {
                visibleCache?.Remove(ship);
            }

            if (!ship.IsMinionShip && !ship.IsCarrierShip)
            {
                ship.FleetShip.IsLoadedIntoLevel = false;
            }
            Ships.Remove(ship);
            int shipSideIndex = ship.Side - 1;
            if (shipSideIndex >= 0 && shipSideIndex < ShipsBySide.Length)
            {
                ShipsBySide[shipSideIndex].Remove(ship);
            }
            MiningShips.Remove(ship);
            ShipsById.Remove(ship.Id);
            if (!ShipsToRelease.Contains(ship))
            {
                ShipsToRelease.Add(ship);
            }

            ship.IsMinionShip = false;
            ship.IsCarrierShip = false;
        }

        public void AddDeadBody(ShipRemains body)
        {
            Deadbodies.Add(body);
        }

        public void CleanupRuntimeObjectsForReset()
        {
            _resetProjectiles.Clear();
            _resetProjectiles.AddRange(Projectiles);
            for (int i = 0; i < _resetProjectiles.Count; i++)
            {
                Projectile projectile = _resetProjectiles[i];
                if (projectile != null && !projectile.IsDead)
                {
                    projectile.Kill();
                }
            }

            _resetObstacles.Clear();
            _resetObstacles.AddRange(Obstacles);
            for (int i = 0; i < _resetObstacles.Count; i++)
            {
                Obstacle obstacle = _resetObstacles[i];
                if (obstacle == null || obstacle.IsDead)
                {
                    continue;
                }

                switch (obstacle.ObstacleType)
                {
                    case ConfigData.ObstacleTypes.CollisionAsteroid:
                        ((CollisionAsteroid)obstacle).Kill(true);
                        break;
                    case ConfigData.ObstacleTypes.MiningAsteroid:
                        ((MiningAsteroid)obstacle).Kill(true);
                        break;
                    case ConfigData.ObstacleTypes.AsteroidPiece:
                        ((AsteroidPiece)obstacle).Kill();
                        break;
                }
            }

            Release();
        }

        public void Release()
        {
            foreach (Ship ship in ShipsToRelease)
            {
                ship.PrepareForLevelTeardown();
            }

            List<Ship> ships = DrainReadyShips();
            List<Command> commands = DrainReleaseQueue(CommandsToRelease, _releaseCommands);
            List<Squad> squads = DrainReleaseQueue(SquadsToRelease, _releaseSquads);
            List<CollisionAsteroid> asteroids = DrainReleaseQueue(AsteroidsToRelease, _releaseAsteroids);
            List<AsteroidPiece> asteroidPieces = DrainReleaseQueue(AsteroidPiecesToRelease, _releaseAsteroidPieces);
            List<MiningAsteroid> miningAsteroids = DrainReleaseQueue(MiningAsteroidsToRelease, _releaseMiningAsteroids);

            foreach (Ship ship in ships)
            {
                Stage.Pool.ReturnShipToPool(ship);
            }
            foreach (Command command in commands)
            {
                Stage.Pool.ReturnCommandToPool(command);
            }
            foreach (Squad squad in squads)
            {
                Stage.Pool.ReturnSquadToPool(squad);
            }
            foreach (CollisionAsteroid asteroid in asteroids)
            {
                Stage.Pool.ReturnCollisionAsteroidToPool(asteroid);
            }
            foreach (AsteroidPiece piece in asteroidPieces)
            {
                Stage.Pool.ReturnAsteroidPieceToPool(piece);
            }
            foreach (MiningAsteroid miningAsteroid in miningAsteroids)
            {
                Stage.Pool.ReturnMiningAsteroidToPool(miningAsteroid);
            }
        }

        private List<Ship> DrainReadyShips()
        {
            _readyShips.Clear();
            _readyShipSet.Clear();
            foreach (Ship ship in ShipsToRelease)
            {
                _projectilesToRemove.Clear();
                foreach (Projectile projectile in ship.ProjectilesInFlight)
                {
                    if (projectile == null || projectile.IsDead || projectile.Shooter != ship)
                    {
                        _projectilesToRemove.Add(projectile);
                    }
                }
                for (int projectileIndex = 0; projectileIndex < _projectilesToRemove.Count; projectileIndex++)
                {
                    ship.ProjectilesInFlight.Remove(_projectilesToRemove[projectileIndex]);
                }

                if (ship.CanReturnToPool())
                {
                    _readyShips.Add(ship);
                    _readyShipSet.Add(ship);
                }
            }

            if (_readyShipSet.Count > 0)
            {
                for (int shipIndex = ShipsToRelease.Count - 1; shipIndex >= 0; shipIndex--)
                {
                    if (_readyShipSet.Contains(ShipsToRelease[shipIndex]))
                    {
                        ShipsToRelease.RemoveAt(shipIndex);
                    }
                }
            }
            return _readyShips;
        }

        private static List<T> DrainReleaseQueue<T>(List<T> queue, List<T> buffer)
        {
            buffer.Clear();
            if (queue.Count == 0)
            {
                return buffer;
            }
            buffer.AddRange(queue);
            queue.Clear();
            return buffer;
        }
    }
}
