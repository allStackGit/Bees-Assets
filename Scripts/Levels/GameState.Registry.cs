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
            squad.SavedSquad.IsLoadedIntoLevel = true;
            Squads.Add(squad);
            OriginalSquadCounts[squad.Side - 1]++;
        }

        public void RemoveSquad(Squad squad)
        {
            squad.SavedSquad.IsLoadedIntoLevel = false;
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
            ship.FleetShip.IsLoadedIntoLevel = false;
            Ships.Remove(ship);
            MiningShips.Remove(ship);
            ShipsById.Remove(ship.Id);
            if (!ShipsToRelease.Contains(ship))
            {
                ShipsToRelease.Add(ship);
            }
        }

        public void AddDeadBody(ShipRemains body)
        {
            Deadbodies.Add(body);
        }

        public void Release()
        {
            // A dead Ship wrapper remains authoritative for its in-flight projectiles.
            // Keep it out of the pool until every active projectile has stopped using it.
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
                // Projectile.Kill historically leaves an entry behind when ShipIsDead is
                // true. Once a projectile is dead or has been pooled/reused by another
                // shooter, that reference no longer keeps this dead shooter alive.
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
