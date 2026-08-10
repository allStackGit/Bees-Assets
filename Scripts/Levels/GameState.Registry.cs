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
            if (!ship.IsMinionShip && !ship.IsCarrierShip)
            {
                ship.FleetShip.IsLoadedIntoLevel = true;
            }
            Ships.Add(ship);
            ShipsById.Add(ship.Id, ship);
            if (ship.IsHiveMindControlled)
            {
                HivemindShips[ship.Side - 1][ship.Id] =
                    new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
            }
        }

        public void AddSquad(Squad squad)
        {
            // Minion/Carrier squads share their parent's SavedSquad identity. They are
            // transient children and must not claim persisted ownership or change the
            // player's normal squad-count/hotkey range. Give them a unique runtime number
            // without treating them as normal selectable squads.
            if (squad.IsMinionSquad)
            {
                squad.SquadNumber = Squads
                    .Where(existing => existing.Side == squad.Side)
                    .Select(existing => existing.SquadNumber)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
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

            // Do not scan/mutate the global socket request set from the synchronous casualty
            // path. CommandRequest/MatchupStrategyRequest retain the runtime ItemId and reject
            // dead or recycled squads when a response arrives, so a stale response cannot be
            // applied to a new pooled lifecycle. This keeps squad death bounded by local state.
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

            // A dead Hivemind observer must stop contributing visibility immediately. Do not
            // scan every other observer's HashSet to remove this ship as a seen target: live
            // visibility aggregation already filters dead ships, and ordinary ship wrappers
            // are not returned to the pool until ResetState clears these registries. Keeping
            // casualty cleanup bounded avoids an O(observer-count) sweep on every ship death.
            if (ship.IsHiveMindControlled &&
                ship.Side >= 1 && ship.Side <= HivemindShips.Length &&
                HivemindShips[ship.Side - 1] != null)
            {
                HivemindShips[ship.Side - 1].Remove(ship.Id);
            }
            foreach (HashSet<Ship> visibleCache in VisionCache)
            {
                visibleCache?.Remove(ship);
            }

            // ShipDamageStatuses and SpottedShips may still contain this dead wrapper until
            // reset. Their live readers either validate the target before use or are inactive,
            // and ResetState clears both registries before any ship wrapper can be reused. Do
            // not traverse both registries synchronously on every casualty for a pool-reuse
            // scenario that cannot occur during the live level.

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
            // Release() is a teardown boundary. Presentation-only death delays must not retain
            // or later mutate pooled wrappers after the owning Level is ending/resetting.
            foreach (Ship ship in ShipsToRelease)
            {
                ship.PrepareForLevelTeardown();
            }

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

            Ship[] ready = ShipsToRelease.Where(ship => ship.CanReturnToPool()).ToArray();
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
