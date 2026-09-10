using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Episode-scoped combat telemetry for dedicated RL training. Metrics are updated only by actual
/// shot and damage events, so diagnostics do not add a per-frame fleet/turret scan or a second log line.
/// </summary>
internal static class RlOneVsOneCombatTelemetry
{
    private const float AccurateAimThresholdDegrees = 5f;

    private static Level _level;
    private static int _beeSide;
    private static int _humanSide;
    private static bool _active;
    private static float _episodeMapSize = -1f;

    private static readonly double[] AimErrorDegrees = new double[2];
    private static readonly long[] AimSamples = new long[2];
    private static readonly long[] AccurateAimSamples = new long[2];
    private static readonly long[] AlignedTurretSamples = new long[2];
    private static readonly float[] FirstFireDistance = { -1f, -1f };
    private static readonly float[] FirstHitDistance = { -1f, -1f };

    internal static void Begin(Level level)
    {
        Reset();
        if (level == null || level.State == null || ConfigData.Configuration == null)
        {
            return;
        }

        _level = level;
        _beeSide = ConfigData.Configuration.BeeSide;
        _humanSide = ConfigData.Configuration.HumanSide;
        _episodeMapSize = RlOneVsOneArenaMapSizeState.GetMapSize(level);
        _active = true;
    }

    internal static void End(Level level)
    {
        if (_active && level == _level)
        {
            _active = false;
            _level = null;
        }
    }

    /// <summary>
    /// Samples range and aim quality only when an RL-controlled turret actually launches a projectile.
    /// RL point-fire can intentionally have no TargetShip, so aim error is measured against the
    /// best-aligned live enemy from the policy's requested aim point.
    /// </summary>
    internal static void RecordShotFired(Ship ship, Weapon weapon)
    {
        if (!TryGetSideIndex(ship, out int sideIndex) || !(weapon is Turret turret) || !turret.IsRlControlled)
        {
            return;
        }

        Vector2 origin = turret.GetPosition();
        if (TryFindBestAimedEnemy(_level, ship.Side, origin, turret.RlTargetPoint, out float distance, out float errorDegrees))
        {
            if (FirstFireDistance[sideIndex] < 0f)
            {
                FirstFireDistance[sideIndex] = distance;
            }

            AimSamples[sideIndex]++;
            AimErrorDegrees[sideIndex] += errorDegrees;
            if (errorDegrees <= AccurateAimThresholdDegrees)
            {
                AccurateAimSamples[sideIndex]++;
            }
            if (turret.IsAimedAtTarget)
            {
                AlignedTurretSamples[sideIndex]++;
            }
            return;
        }

        if (FirstFireDistance[sideIndex] < 0f)
        {
            FirstFireDistance[sideIndex] = FindNearestEnemyDistance(_level, ship);
        }
    }

    /// <summary>
    /// Captures separation at the first actual enemy damage event for each side. This deliberately
    /// includes gun, bomb, charge, and explosion damage so it describes first effective contact.
    /// </summary>
    internal static void RecordHit(Ship sourceShip, Ship target, int damage)
    {
        if (damage <= 0 || !TryGetSideIndex(sourceShip, out int sourceIndex) ||
            !TryGetSideIndex(target, out int targetIndex) || sourceIndex == targetIndex ||
            FirstHitDistance[sourceIndex] >= 0f)
        {
            return;
        }

        FirstHitDistance[sourceIndex] = Vector2.Distance(sourceShip.GetPosition(), target.GetPosition());
    }

    internal static string BuildEpisodeFields()
    {
        return $"map_size={FormatDistance(_episodeMapSize)} " +
               $"bee_aim_samples={AimSamples[0]} bee_aim_error={FormatAimError(0)} bee_aim_within_5deg={FormatPercent(AccurateAimSamples[0], AimSamples[0])} " +
               $"bee_turret_aligned={FormatPercent(AlignedTurretSamples[0], AimSamples[0])} bee_first_fire_distance={FormatDistance(FirstFireDistance[0])} bee_first_hit_distance={FormatDistance(FirstHitDistance[0])} " +
               $"human_aim_samples={AimSamples[1]} human_aim_error={FormatAimError(1)} human_aim_within_5deg={FormatPercent(AccurateAimSamples[1], AimSamples[1])} " +
               $"human_turret_aligned={FormatPercent(AlignedTurretSamples[1], AimSamples[1])} human_first_fire_distance={FormatDistance(FirstFireDistance[1])} human_first_hit_distance={FormatDistance(FirstHitDistance[1])}";
    }

    private static bool TryFindBestAimedEnemy(
        Level level,
        int firingSide,
        Vector2 origin,
        Vector2 aimPoint,
        out float distance,
        out float errorDegrees)
    {
        distance = -1f;
        errorDegrees = 180f;
        if (!_active || level == null || level.State == null)
        {
            return false;
        }

        Vector2 aimDirection = aimPoint - origin;
        if (aimDirection.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        int enemySide = firingSide == _beeSide ? _humanSide : _beeSide;
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

    private static float FindNearestEnemyDistance(Level level, Ship ship)
    {
        if (!_active || level == null || level.State == null || ship == null)
        {
            return -1f;
        }

        int enemySide = ship.Side == _beeSide ? _humanSide : _beeSide;
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

    private static bool TryGetSideIndex(Ship ship, out int sideIndex)
    {
        sideIndex = -1;
        if (!_active || ship == null || ship.Level != _level)
        {
            return false;
        }
        if (ship.Side == _beeSide)
        {
            sideIndex = 0;
            return true;
        }
        if (ship.Side == _humanSide)
        {
            sideIndex = 1;
            return true;
        }
        return false;
    }

    private static string FormatAimError(int sideIndex)
    {
        return AimSamples[sideIndex] == 0
            ? "none"
            : $"{(AimErrorDegrees[sideIndex] / AimSamples[sideIndex]):F2}deg";
    }

    private static string FormatPercent(long numerator, long denominator)
    {
        return denominator <= 0 ? "none" : $"{((double)numerator / denominator):P2}";
    }

    private static string FormatDistance(float value)
    {
        return value < 0f ? "none" : $"{value:F2}";
    }

    private static void Reset()
    {
        _level = null;
        _beeSide = 0;
        _humanSide = 0;
        _active = false;
        _episodeMapSize = -1f;
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            AimErrorDegrees[sideIndex] = 0d;
            AimSamples[sideIndex] = 0;
            AccurateAimSamples[sideIndex] = 0;
            AlignedTurretSamples[sideIndex] = 0;
            FirstFireDistance[sideIndex] = -1f;
            FirstHitDistance[sideIndex] = -1f;
        }
    }
}
