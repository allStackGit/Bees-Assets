using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;

namespace Assets.Scripts.Levels
{
    public partial class GameState
    {
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
            ship.FleetShip.IsLoadedIntoLevel = true;
            Ships.Add(ship);
            ShipsById.Add(ship.Id, ship);
        }

        public void AddSquad(Squad squad)
        {
            // Minion/Carrier squads share their parent's SavedSquad identity. They are
            // transient children and must not claim or release the persisted squad's
            // loaded-state ownership independently of the parent squad.
            if (!squad.IsMinionSquad)
            {
                squad.SavedSquad.IsLoadedIntoLevel = true;
            }
            Squads.Add(squad);
            OriginalSquadCounts[squad.Side - 1]++;
        }

        public void RemoveSquad(Squad squad)
        {
            if (!squad.IsMinionSquad)
            {
                squad.SavedSquad.IsLoadedIntoLevel = false;
            }
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

            // These records retain the live Ship wrapper, whose runtime Id changes when
            // the object is reused from the pool. Remove them while the old identity is
            // still authoritative so stale combat/spotting state cannot attach to the
            // next ship that occupies this wrapper. ResetLevel temporarily clears the
            // per-side spotted-list slots before killing the old ships, so tolerate that
            // teardown state; ResetState recreates both lists for the next episode.
            foreach (List<ShipDamageStatus> statuses in ShipDamageStatuses)
            {
                if (statuses != null)
                {
                    statuses.RemoveAll(status => status == null || status.Ship == null || status.Ship == ship);
                }
            }
            foreach (List<SpottedShip> spotted in SpottedShips)
            {
                if (spotted != null)
                {
                    spotted.RemoveAll(entry => entry == null || entry.Ship == null || entry.Ship == ship);
                }
            }

            // Queen/Scout minions and Carrier children intentionally replace their
            // transient FleetShip with the parent's FleetShip for shared stat accounting.
            // Their teardown must not mark the still-live parent FleetShip as unloaded.
            if (!ship.IsMinionShip && !ship.IsCarrierShip)
            {
                ship.FleetShip.IsLoadedIntoLevel = false;
            }
            Ships.Remove(ship);
            MiningShips.Remove(ship);
            ShipsById.Remove(ship.Id);
            if (!ShipsToRelease.Contains(ship))
            {
                ShipsToRelease.Add(ship);
            }

            // Drone/Striker pools can serve both ordinary ships and Carrier children, and
            // spawned ship pools can later serve another lifecycle. Clear role flags only
            // after ownership-sensitive deregistration has completed.
            ship.IsMinionShip = false;
            ship.IsCarrierShip = false;
        }

        public void AddDeadBody(ShipRemains body)
        {
            Deadbodies.Add(body);
        }

        public void CleanupRuntimeObjectsForReset()
        {
            // Neural-network ResetLevel bypasses SaveAndEnd. Tear down active transient
            // objects here before ResetState clears the registries that own them.
            foreach (Projectile projectile in Projectiles.ToList())
            {
                if (projectile != null && !projectile.IsDead)
                {
                    projectile.Kill();
                }
            }

            foreach (Obstacle obstacle in Obstacles.ToList())
            {
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
            Ship[] ships = DrainReadyShips();
            Command[] commands = DrainReleaseQueue(CommandsToRelease);
            Squad[] squads = DrainReleaseQueue(SquadsToRelease);
            CollisionAsteroid[] asteroids = DrainReleaseQueue(AsteroidsToRelease);
            AsteroidPiece[] asteroidPieces = DrainReleaseQueue(AsteroidPiecesToRelease);
            MiningAsteroid[] miningAsteroids = DrainReleaseQueue(MiningAsteroidsToRelease);

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

        private Ship[] DrainReadyShips()
        {
            foreach (Ship ship in ShipsToRelease)
            {
                ship.ProjectilesInFlight.RemoveWhere(projectile =>
                    projectile == null || projectile.IsDead || projectile.Shooter != ship);
            }

            Ship[] ready = ShipsToRelease.Where(ship => ship.ProjectilesInFlight.Count == 0).ToArray();
            if (ready.Length > 0)
            {
                HashSet<Ship> readySet = ready.ToHashSet();
                ShipsToRelease.RemoveAll(readySet.Contains);
            }
            return ready;
        }

        private static T[] DrainReleaseQueue<T>(List<T> queue)
        {
            T[] items = queue.ToArray();
            queue.Clear();
            return items;
        }
    }
}
