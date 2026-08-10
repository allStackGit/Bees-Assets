using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Server;

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

            // A server response can arrive well after this squad has died. Remove pending
            // requests while the old ItemId is still authoritative so reconnect/resend logic
            // cannot keep transmitting work that can only be rejected after pool reuse.
            if (Level != null && Level.IsLevelSetupOnServer)
            {
                int removedSquadItemId = squad.ItemId;
                ConfigData.Socket.StandingRequests.RemoveWhere(request =>
                    (request is CommandRequest commandRequest &&
                     ReferenceEquals(commandRequest.Squad, squad) &&
                     commandRequest.SquadId == removedSquadItemId) ||
                    (request is MatchupStrategyRequest matchupRequest &&
                     ReferenceEquals(matchupRequest.Squad, squad) &&
                     matchupRequest.SquadId == removedSquadItemId));
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

            // Hivemind visibility is live runtime state. Remove this lifecycle both as an
            // observer and as a seen target before the pooled wrapper receives a new Id.
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
