using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
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

            // Other live ships can retain this pooled wrapper in follow/proximity/contact
            // state. Unity does not guarantee a trigger-exit callback when the target is
            // disabled during the same physics step, and IsDead becomes false again when
            // the wrapper is reused. Invalidate those references before pool ownership can
            // change so a new lifecycle cannot silently become the old target.
            foreach (Ship observer in Ships)
            {
                if (observer == null || ReferenceEquals(observer, ship))
                {
                    continue;
                }

                if (ReferenceEquals(observer.TargetEnemyShipToFollow, ship))
                {
                    observer.TargetEnemyShipToFollow = null;
                }
                if (observer.HasProximityCollider && observer.ProximityCollider != null)
                {
                    observer.ProximityCollider.NearbyEnemyShips.Remove(ship);
                }
                if (observer.HasWeapons && observer.Weapons != null)
                {
                    foreach (Weapon weapon in observer.Weapons)
                    {
                        if (weapon == null)
                        {
                            continue;
                        }

                        weapon.ShipsWithinRange.Remove(ship.Id);
                        if (weapon.CachedTargetingQueue.RemoveAll(candidate => ReferenceEquals(candidate, ship)) > 0)
                        {
                            weapon.HasCachedChanged = true;
                        }
                        if (weapon is BeamCannon beamCannon && ReferenceEquals(beamCannon.LaserBeamTarget, ship))
                        {
                            beamCannon.LaserBeamTarget = null;
                        }
                        if (ReferenceEquals(weapon.TargetShip, ship))
                        {
                            if (weapon is Bomb bomb)
                            {
                                bomb.ReleaseTargetReservation();
                            }
                            else
                            {
                                weapon.ClearTargets();
                            }
                        }
                    }
                }
                if (observer is Striker striker)
                {
                    if (ReferenceEquals(striker.TouchingShip, ship)) striker.TouchingShip = null;
                    if (ReferenceEquals(striker.ContactedShip, ship)) striker.ContactedShip = null;
                }
                else if (observer is YellowJacket yellowJacket)
                {
                    if (ReferenceEquals(yellowJacket.TouchingShip, ship)) yellowJacket.TouchingShip = null;
                    if (ReferenceEquals(yellowJacket.ContactedShip, ship)) yellowJacket.ContactedShip = null;
                }
            }

            // Command target queues can outlive an individual target and can also be prepared
            // before becoming the squad's active command. Remove the departing wrapper from
            // both active and scripted queues before a pooled lifecycle can reuse it.
            Beehive departingBeehive = ship as Beehive;
            foreach (Squad squad in Squads)
            {
                Command activeCommand = squad?.GetCommand();
                ForgetShipFromCommandQueues(activeCommand, ship);
                if (departingBeehive != null && activeCommand is Heal activeHeal)
                {
                    activeHeal.BeehiveBecameUnavailable(departingBeehive);
                }
                if (squad?.CommandQueue == null)
                {
                    continue;
                }
                foreach (Command queuedCommand in squad.CommandQueue)
                {
                    ForgetShipFromCommandQueues(queuedCommand, ship);
                }
            }

            // Active projectiles can outlive their target. Purge queued contacts, target
            // reservations and subclass hit histories before this wrapper can be reused.
            foreach (Projectile projectile in Projectiles.ToList())
            {
                if (projectile != null && !projectile.IsDead)
                {
                    projectile.ForgetShip(ship);
                }
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

        private static void ForgetShipFromCommandQueues(Command command, Ship ship)
        {
            if (command == null || ship == null)
            {
                return;
            }
            if (command.OriginalQueue.Count > 0)
            {
                command.OriginalQueue = new Queue<Ship>(command.OriginalQueue.Where(candidate => !ReferenceEquals(candidate, ship)));
            }
            if (command.TargetingQueue.Count > 0)
            {
                command.TargetingQueue = new Queue<Ship>(command.TargetingQueue.Where(candidate => !ReferenceEquals(candidate, ship)));
            }
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
            foreach (MiningAsteroid miningAsteroid in MiningAsteroidsToRelease)
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
