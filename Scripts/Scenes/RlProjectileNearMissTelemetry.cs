using System.Collections.Generic;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using UnityEngine;

/// <summary>
/// Episode-local, root-projectile shot outcomes for dedicated RL. Runs no global per-frame
/// scan: only active turret projectiles visit the near-distance broadphase while in flight.
/// Explosions and splitter children inherit the original shot rather than earning duplicates.
/// </summary>
internal static class RlProjectileNearMissTelemetry
{
    private sealed class Shot
    {
        internal int SideIndex;
        internal int Active = 1;
        internal bool Contact;
        internal bool Near;
    }

    private sealed class Arena
    {
        internal int BeeSide;
        internal int HumanSide;
        internal readonly Dictionary<long, Shot> Shots = new Dictionary<long, Shot>();
        internal readonly long[] Completed = new long[2];
        internal readonly long[] Missed = new long[2];
        internal readonly long[] NearMissed = new long[2];
    }

    private static readonly Dictionary<Level, Arena> Arenas = new Dictionary<Level, Arena>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Reset() => Arenas.Clear();

    internal static void Begin(Level level, int beeSide, int humanSide)
    {
        Arenas[level] = new Arena { BeeSide = beeSide, HumanSide = humanSide };
    }

    internal static void End(Level level)
    {
        if (level != null)
        {
            Arenas.Remove(level);
        }
    }

    internal static void Launched(Projectile projectile)
    {
        if (projectile == null || projectile.RlRootProjectileId <= 0 ||
            !(projectile.Weapon is Turret turret) || !turret.IsRlControlled ||
            projectile.Shooter == null ||
            RlPlayerDerivedActionReplay.IsScriptedSide(projectile.Level, projectile.Shooter.Side) ||
            !Arenas.TryGetValue(projectile.Level, out Arena arena))
        {
            return;
        }

        int sideIndex = projectile.Shooter.Side == arena.BeeSide ? 0 :
            projectile.Shooter.Side == arena.HumanSide ? 1 : -1;
        if (sideIndex >= 0 && !arena.Shots.ContainsKey(projectile.RlRootProjectileId))
        {
            arena.Shots.Add(projectile.RlRootProjectileId, new Shot { SideIndex = sideIndex });
            projectile.RlTrackShot = true;
        }
    }

    internal static void Continuation(Projectile projectile)
    {
        if (projectile != null && Arenas.TryGetValue(projectile.Level, out Arena arena) &&
            arena.Shots.TryGetValue(projectile.RlRootProjectileId, out Shot shot))
        {
            shot.Active++;
            projectile.RlTrackShot = true;
        }
    }

    internal static void EnemyContact(Projectile projectile, Ship enemy)
    {
        if (projectile == null || enemy == null || projectile.Shooter == null ||
            enemy.Side == projectile.Shooter.Side)
        {
            return;
        }
        if (Arenas.TryGetValue(projectile.Level, out Arena arena) &&
            arena.Shots.TryGetValue(projectile.RlRootProjectileId, out Shot shot))
        {
            shot.Contact = true;
        }
    }

    internal static void Travel(Projectile projectile, Vector2 lastLocalPosition)
    {
        if (projectile == null || projectile.Shooter == null || projectile.Collider == null ||
            projectile.Level == null || projectile.Level.Map == null ||
            projectile.Level.State == null ||
            !Arenas.TryGetValue(projectile.Level, out Arena arena) ||
            !arena.Shots.TryGetValue(projectile.RlRootProjectileId, out Shot shot) ||
            shot.Near || shot.Contact)
        {
            return;
        }

        Vector2 start = projectile.Level.Map.Transform.TransformPoint(lastLocalPosition);
        Vector2 end = projectile.Transform.position;
        Vector2 movement = end - start;
        float movementSquared = movement.sqrMagnitude;
        Bounds projectileBounds = projectile.Collider.bounds;
        Bounds sweep = projectileBounds;
        sweep.Encapsulate(new Bounds(
            new Vector3(start.x, start.y, projectileBounds.center.z),
            projectileBounds.size));
        float nearDistance = RlOneVsOneReward.NearMissDistance;
        sweep.Expand(nearDistance * 2f);

        int enemySide = shot.SideIndex == 0 ? arena.HumanSide : arena.BeeSide;
        List<Ship> enemies = projectile.Level.State.GetShips(enemySide);
        for (int i = 0; i < enemies.Count; i++)
        {
            Ship enemy = enemies[i];
            if (enemy == null || enemy.IsDead || enemy.Collider == null ||
                !sweep.Intersects(enemy.Collider.bounds))
            {
                continue;
            }

            // Closest point along the swept centerline, then exact point-to-enemy-hull
            // separation. The projectile's collider radius is included conservatively.
            Vector2 enemyCenter = enemy.Collider.bounds.center;
            float t = movementSquared > 0.000001f
                ? Mathf.Clamp01(Vector2.Dot(enemyCenter - start, movement) / movementSquared)
                : 0f;
            Vector2 closestPass = start + t * movement;
            Vector2 enemyHull = enemy.Collider.ClosestPoint(closestPass);
            float projectileRadius = Mathf.Min(projectileBounds.extents.x,
                projectileBounds.extents.y);
            float threshold = nearDistance + projectileRadius;
            if ((closestPass - enemyHull).sqrMagnitude <= threshold * threshold ||
                projectile.Collider.Distance(enemy.Collider).distance <= nearDistance)
            {
                shot.Near = true;
                return;
            }
        }
    }

    internal static void Ended(Projectile projectile)
    {
        if (projectile == null || !Arenas.TryGetValue(projectile.Level, out Arena arena) ||
            !arena.Shots.TryGetValue(projectile.RlRootProjectileId, out Shot shot))
        {
            return;
        }

        if (--shot.Active > 0)
        {
            return;
        }

        arena.Shots.Remove(projectile.RlRootProjectileId);
        arena.Completed[shot.SideIndex]++;
        if (!shot.Contact)
        {
            arena.Missed[shot.SideIndex]++;
            if (shot.Near)
            {
                arena.NearMissed[shot.SideIndex]++;
            }
            RlOneVsOneEpisodeCoordinator.RecordResolvedProjectileMiss(projectile.Shooter, shot.Near);
        }
    }

    internal static void AppendEpisodeData(Level level, int sideIndex, Dictionary<string, object> combat)
    {
        if (!Arenas.TryGetValue(level, out Arena arena))
        {
            return;
        }

        long inFlight = 0;
        foreach (Shot shot in arena.Shots.Values)
        {
            if (shot.SideIndex == sideIndex)
            {
                inFlight++;
            }
        }
        combat["completed_projectile_shots"] = arena.Completed[sideIndex];
        combat["missed_projectile_shots"] = arena.Missed[sideIndex];
        combat["near_missed_projectile_shots_within_2"] = arena.NearMissed[sideIndex];
        combat["in_flight_projectile_shots"] = inFlight;
    }
}
