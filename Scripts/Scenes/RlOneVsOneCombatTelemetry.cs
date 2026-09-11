using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Episode-scoped combat telemetry for dedicated RL training. Metrics are updated only by actual
/// shot and damage events, so diagnostics do not add a per-frame fleet/turret scan or a second log line.
/// Mutable telemetry is partitioned by Level so simultaneous arenas cannot reset or consume each other's state.
/// </summary>
internal static class RlOneVsOneCombatTelemetry
{
    private const float AccurateAimThresholdDegrees = 5f;

    private sealed class ArenaState
    {
        internal readonly Level Level;
        internal readonly int BeeSide;
        internal readonly int HumanSide;
        internal readonly float EpisodeMapSize;
        internal readonly double[] AimErrorDegrees = new double[2];
        internal readonly long[] AimSamples = new long[2];
        internal readonly long[] AccurateAimSamples = new long[2];
        internal readonly long[] AlignedTurretSamples = new long[2];
        internal readonly float[] FirstFireDistance = { -1f, -1f };
        internal readonly float[] FirstHitDistance = { -1f, -1f };

        internal ArenaState(Level level, int beeSide, int humanSide, float episodeMapSize)
        {
            Level = level;
            BeeSide = beeSide;
            HumanSide = humanSide;
            EpisodeMapSize = episodeMapSize;
        }
    }

    private static readonly Dictionary<Level, ArenaState> States = new Dictionary<Level, ArenaState>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ResetStateRegistry()
    {
        States.Clear();
    }

    internal static void Begin(Level level)
    {
        if (level == null || level.State == null || ConfigData.Configuration == null)
        {
            return;
        }

        States[level] = new ArenaState(
            level,
            ConfigData.Configuration.BeeSide,
            ConfigData.Configuration.HumanSide,
            RlOneVsOneArenaMapSizeState.GetMapSize(level));
    }

    internal static void End(Level level)
    {
        if (level != null)
        {
            States.Remove(level);
        }
    }

    /// <summary>
    /// Samples range and aim quality only when an RL-controlled turret actually launches a projectile.
    /// RL point-fire can intentionally have no TargetShip, so aim error is measured against the
    /// best-aligned live enemy from the policy's requested aim point.
    /// </summary>
    internal static void RecordShotFired(Ship ship, Weapon weapon)
    {
        if (!TryGetSideIndex(ship, out ArenaState state, out int sideIndex) ||
            !(weapon is Turret turret) || !turret.IsRlControlled)
        {
            return;
        }

        Vector2 origin = turret.GetPosition();
        if (TryFindBestAimedEnemy(state, ship.Side, origin, turret.RlTargetPoint, out float distance, out float errorDegrees))
        {
            if (state.FirstFireDistance[sideIndex] < 0f)
            {
                state.FirstFireDistance[sideIndex] = distance;
            }

            state.AimSamples[sideIndex]++;
            state.AimErrorDegrees[sideIndex] += errorDegrees;
            if (errorDegrees <= AccurateAimThresholdDegrees)
            {
                state.AccurateAimSamples[sideIndex]++;
            }
            if (turret.IsAimedAtTarget)
            {
                state.AlignedTurretSamples[sideIndex]++;
            }
            return;
        }

        if (state.FirstFireDistance[sideIndex] < 0f)
        {
            state.FirstFireDistance[sideIndex] = FindNearestEnemyDistance(state, ship);
        }
    }

    /// <summary>
    /// Captures separation at the first actual enemy damage event for each side. This deliberately
    /// includes gun, bomb, charge, and explosion damage so it describes first effective contact.
    /// </summary>
    internal static void RecordHit(Ship sourceShip, Ship target, int damage)
    {
        if (damage <= 0 ||
            !TryGetSideIndex(sourceShip, out ArenaState sourceState, out int sourceIndex) ||
            !TryGetSideIndex(target, out ArenaState targetState, out int targetIndex) ||
            sourceState != targetState || sourceIndex == targetIndex ||
            sourceState.FirstHitDistance[sourceIndex] >= 0f)
        {
            return;
        }

        sourceState.FirstHitDistance[sourceIndex] = Vector2.Distance(sourceShip.GetPosition(), target.GetPosition());
    }

    internal static string BuildEpisodeFields(Level level)
    {
        if (!TryGetState(level, out ArenaState state))
        {
            return "map_size=none " +
                   "bee_aim_samples=0 bee_aim_error=none bee_aim_within_5deg=none bee_turret_aligned=none bee_first_fire_distance=none bee_first_hit_distance=none " +
                   "human_aim_samples=0 human_aim_error=none human_aim_within_5deg=none human_turret_aligned=none human_first_fire_distance=none human_first_hit_distance=none";
        }

        return $"map_size={FormatDistance(state.EpisodeMapSize)} " +
               $"bee_aim_samples={state.AimSamples[0]} bee_aim_error={FormatAimError(state, 0)} bee_aim_within_5deg={FormatPercent(state.AccurateAimSamples[0], state.AimSamples[0])} " +
               $"bee_turret_aligned={FormatPercent(state.AlignedTurretSamples[0], state.AimSamples[0])} bee_first_fire_distance={FormatDistance(state.FirstFireDistance[0])} bee_first_hit_distance={FormatDistance(state.FirstHitDistance[0])} " +
               $"human_aim_samples={state.AimSamples[1]} human_aim_error={FormatAimError(state, 1)} human_aim_within_5deg={FormatPercent(state.AccurateAimSamples[1], state.AimSamples[1])} " +
               $"human_turret_aligned={FormatPercent(state.AlignedTurretSamples[1], state.AimSamples[1])} human_first_fire_distance={FormatDistance(state.FirstFireDistance[1])} human_first_hit_distance={FormatDistance(state.FirstHitDistance[1])}";
    }

    private static bool TryFindBestAimedEnemy(
        ArenaState state,
        int firingSide,
        Vector2 origin,
        Vector2 aimPoint,
        out float distance,
        out float errorDegrees)
    {
        distance = -1f;
        errorDegrees = 180f;
        Level level = state?.Level;
        if (level == null || level.State == null)
        {
            return false;
        }

        Vector2 aimDirection = aimPoint - origin;
        if (aimDirection.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        int enemySide = firingSide == state.BeeSide ? state.HumanSide : state.BeeSide;
        List<Ship> enemies = level.State.GetShips(enemySide);
        bool found = false;
        for (int i = 0; i < enemies.Count; i++)
        {
            Ship enemy = enemies[i];
            if (enemy == null || enemy.IsDead)
            {
                continue;
            }

            Vector2 enemyDirection = enemy.GetPosition() - origin;
            if (enemyDirection.sqrMagnitude <= 0.000001f)
            {
                continue;
            }

            float error = Vector2.Angle(aimDirection, enemyDirection);
            if (!found || error < errorDegrees)
            {
                found = true;
                errorDegrees = error;
                distance = enemyDirection.magnitude;
            }
        }
        return found;
    }

    private static float FindNearestEnemyDistance(ArenaState state, Ship ship)
    {
        Level level = state?.Level;
        if (level == null || level.State == null || ship == null)
        {
            return -1f;
        }

        int enemySide = ship.Side == state.BeeSide ? state.HumanSide : state.BeeSide;
        List<Ship> enemies = level.State.GetShips(enemySide);
        float nearest = float.MaxValue;
        for (int i = 0; i < enemies.Count; i++)
        {
            Ship enemy = enemies[i];
            if (enemy == null || enemy.IsDead)
            {
                continue;
            }
            nearest = Mathf.Min(nearest, Vector2.Distance(ship.GetPosition(), enemy.GetPosition()));
        }
        return nearest == float.MaxValue ? -1f : nearest;
    }

    private static bool TryGetSideIndex(Ship ship, out ArenaState state, out int sideIndex)
    {
        state = null;
        sideIndex = -1;
        if (ship == null || !TryGetState(ship.Level, out state))
        {
            return false;
        }
        if (ship.Side == state.BeeSide)
        {
            sideIndex = 0;
            return true;
        }
        if (ship.Side == state.HumanSide)
        {
            sideIndex = 1;
            return true;
        }
        return false;
    }

    private static bool TryGetState(Level level, out ArenaState state)
    {
        state = null;
        return level != null && States.TryGetValue(level, out state) && state != null;
    }

    private static string FormatAimError(ArenaState state, int sideIndex)
    {
        return state.AimSamples[sideIndex] == 0
            ? "none"
            : $"{(state.AimErrorDegrees[sideIndex] / state.AimSamples[sideIndex]):F2}deg";
    }

    private static string FormatPercent(long numerator, long denominator)
    {
        return denominator <= 0 ? "none" : $"{((double)numerator / denominator):P2}";
    }

    private static string FormatDistance(float value)
    {
        return value < 0f ? "none" : $"{value:F2}";
    }

    internal static void SetStateForTests(Level level, int beeSide, int humanSide, float episodeMapSize)
    {
        if (level != null)
        {
            States[level] = new ArenaState(level, beeSide, humanSide, episodeMapSize);
        }
    }

    internal static int GetTrackedLevelCountForTests()
    {
        return States.Count;
    }

    internal static void ResetForTests()
    {
        States.Clear();
    }
}
