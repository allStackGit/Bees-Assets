using Assets.Scripts;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Episode-scoped combat telemetry for dedicated RL training. Normal combat metrics follow
/// shot/damage events; both learner and opponent policy decisions accumulate aim samples in memory.
/// Diagnostics add no per-frame fleet/turret scan or second log line.
/// Mutable telemetry is partitioned by Level so simultaneous arenas cannot reset or consume each other's state.
/// </summary>
internal static class RlOneVsOneCombatTelemetry
{
    private const float AccurateAimThresholdDegrees = 5f;
    private const int PolicyBearingSectorCount = 8;

    private sealed class ArenaState
    {
        internal readonly Level Level;
        internal readonly int BeeSide;
        internal readonly int HumanSide;
        internal readonly float EpisodeMapSize;
        internal readonly double[] AimErrorDegrees = new double[2];
        internal readonly long[] AimSamples = new long[2];
        internal readonly long[] AccurateAimSamples = new long[2];


        // Collected for both training roles, even when the ship is moving or not firing.
        internal readonly int[] TeamIds = { -1, -1 };
        internal readonly int[] FrameQuarterTurns = { -1, -1 };
        internal readonly long[] FreshAimSamples = new long[2];
        internal readonly long[] HeldAimSamples = new long[2];
        internal readonly long[][][] TargetRequestedSectors =
        {
            CreateSectorPairCounts(), CreateSectorPairCounts()
        };
        internal readonly long[][][] FreshTargetRequestedSectors =
        {
            CreateSectorPairCounts(), CreateSectorPairCounts()
        };
        internal readonly long[][][] HeldTargetRequestedSectors =
        {
            CreateSectorPairCounts(), CreateSectorPairCounts()
        };
        internal readonly long[] PolicyDecisions = new long[2];
        internal readonly long[] PolicyDecisionsWithKnownEnemy = new long[2];
        internal readonly long[] RequestedAimSamples = new long[2];
        internal readonly double[] RequestedAimErrorDegrees = new double[2];
        internal readonly long[] RequestedAimWithin5 = new long[2];
        internal readonly long[] RequestedAimWithin15 = new long[2];
        internal readonly long[] RequestedAimWithin30 = new long[2];
        internal readonly double[] RequestedAimCosSum = new double[2];
        internal readonly double[] RequestedAimSinSum = new double[2];
        internal readonly long[][] TargetBearingCounts = { new long[PolicyBearingSectorCount], new long[PolicyBearingSectorCount] };
        internal readonly long[][] RequestedBearingCounts = { new long[PolicyBearingSectorCount], new long[PolicyBearingSectorCount] };
        internal readonly double[][] TargetSectorErrorDegrees = { new double[PolicyBearingSectorCount], new double[PolicyBearingSectorCount] };
        internal readonly long[][] TargetSectorWithin15 = { new long[PolicyBearingSectorCount], new long[PolicyBearingSectorCount] };
        internal readonly float[] FirstFireDistance = { -1f, -1f };
        internal readonly float[] FirstHitDistance = { -1f, -1f };
        internal readonly HashSet<long>[] EnemyHitProjectiles =
        {
            new HashSet<long>(),
            new HashSet<long>()
        };

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
        RlProjectileNearMissTelemetry.Begin(
            level, ConfigData.Configuration.BeeSide, ConfigData.Configuration.HumanSide);
    }

    internal static void End(Level level)
    {
        if (level != null)
        {
            RlProjectileNearMissTelemetry.End(level);
            States.Remove(level);
        }
    }

    /// <summary>
    /// Capture aim on every policy decision, including moving agents and unfired shots.
    /// FreshAimSlotMask distinguishes a new aim action from the previously held aim
    /// when its continuous output falls inside the policy's aim dead zone.
    /// Enemy visibility matches policy observations; sectors are policy-relative.
    /// </summary>
    internal static void RecordPolicyDecision(
        Ship ship, int teamId, int frameQuarterTurns, Vector2[] requestedWorldAims,
        int freshAimSlotMask)
    {
        if (!TryGetSideIndex(ship, out ArenaState state, out int sideIndex))
        {
            return;
        }

        state.PolicyDecisions[sideIndex]++;
        state.TeamIds[sideIndex] = teamId;
        state.FrameQuarterTurns[sideIndex] = frameQuarterTurns;
        Ship nearestEnemy = null;
        float nearestDistanceSquared = float.MaxValue;
        Vector2 shipPosition = ship.GetPosition();
        foreach (Ship enemy in state.Level.State.GetShipsVisibleToHiveMind(ship.Side))
        {
            if (enemy == null || enemy.IsDead || enemy.Side == ship.Side)
            {
                continue;
            }

            float distanceSquared = (enemy.GetPosition() - shipPosition).sqrMagnitude;
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                nearestEnemy = enemy;
            }
        }
        if (nearestEnemy == null)
        {
            return;
        }
        state.PolicyDecisionsWithKnownEnemy[sideIndex]++;

        if (ship.Weapons == null || requestedWorldAims == null)
        {
            return;
        }

        int slotCount = Mathf.Min(ship.Weapons.Count, requestedWorldAims.Length);
        for (int slot = 0; slot < slotCount; slot++)
        {
            if (!(ship.Weapons[slot] is Turret turret))
            {
                continue;
            }

            Vector2 targetDirection = nearestEnemy.GetPosition() - turret.GetPosition();
            Vector2 requestedAim = requestedWorldAims[slot];
            if (targetDirection.sqrMagnitude < 0.000001f || requestedAim.sqrMagnitude < 0.000001f)
            {
                continue;
            }

            Vector2 policyTarget = RlPolicyCoordinateFrame.WorldToPolicy(targetDirection, frameQuarterTurns);
            Vector2 policyAim = RlPolicyCoordinateFrame.WorldToPolicy(requestedAim, frameQuarterTurns);
            float targetAngle = Mathf.Repeat(Mathf.Atan2(policyTarget.y, policyTarget.x) * Mathf.Rad2Deg, 360f);
            float aimAngle = Mathf.Repeat(Mathf.Atan2(policyAim.y, policyAim.x) * Mathf.Rad2Deg, 360f);
            int targetSector = Mathf.Min(PolicyBearingSectorCount - 1, Mathf.FloorToInt(targetAngle / 45f));
            int requestedSector = Mathf.Min(PolicyBearingSectorCount - 1, Mathf.FloorToInt(aimAngle / 45f));
            float errorDegrees = Vector2.Angle(requestedAim, targetDirection);

            state.RequestedAimSamples[sideIndex]++;
            if ((freshAimSlotMask & (1 << slot)) != 0)
            {
                state.FreshAimSamples[sideIndex]++;
                state.FreshTargetRequestedSectors[sideIndex][targetSector][requestedSector]++;
            }
            else
            {
                state.HeldAimSamples[sideIndex]++;
                state.HeldTargetRequestedSectors[sideIndex][targetSector][requestedSector]++;
            }
            state.TargetRequestedSectors[sideIndex][targetSector][requestedSector]++;
            state.RequestedAimErrorDegrees[sideIndex] += errorDegrees;
            if (errorDegrees <= 5f) state.RequestedAimWithin5[sideIndex]++;
            if (errorDegrees <= 15f)
            {
                state.RequestedAimWithin15[sideIndex]++;
                state.TargetSectorWithin15[sideIndex][targetSector]++;
            }
            if (errorDegrees <= 30f) state.RequestedAimWithin30[sideIndex]++;
            state.TargetBearingCounts[sideIndex][targetSector]++;
            state.RequestedBearingCounts[sideIndex][requestedSector]++;
            state.TargetSectorErrorDegrees[sideIndex][targetSector] += errorDegrees;
            state.RequestedAimCosSum[sideIndex] += Mathf.Cos(aimAngle * Mathf.Deg2Rad);
            state.RequestedAimSinSum[sideIndex] += Mathf.Sin(aimAngle * Mathf.Deg2Rad);

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

    /// <summary>
    /// Count each launched turret projectile at most once when it causes actual
    /// enemy health loss. Rocket explosions and split children carry the root
    /// launch ID, so multi-target hits cannot exceed the number of shots fired.
    /// </summary>
    internal static void RecordProjectileEnemyHit(Projectile projectile, Ship target)
    {
        if (projectile == null || target == null ||
            !(projectile.Weapon is Turret) ||
            !TryGetSideIndex(projectile.Shooter, out ArenaState state, out int sideIndex) ||
            !TryGetSideIndex(target, out ArenaState targetState, out int targetIndex) ||
            state != targetState || sideIndex == targetIndex ||
            projectile.Level != state.Level)
        {
            return;
        }

        state.EnemyHitProjectiles[sideIndex].Add(projectile.RlRootProjectileId);
        RlProjectileNearMissTelemetry.EnemyContact(projectile, target);
    }

    internal static int GetProjectileHitCount(Level level, int sideIndex)
    {
        return sideIndex >= 0 && sideIndex < 2 &&
               TryGetState(level, out ArenaState state)
            ? state.EnemyHitProjectiles[sideIndex].Count
            : 0;
    }

    internal static string BuildEpisodeFields(Level level)
    {
        if (!TryGetState(level, out ArenaState state))
        {
            return "map_size=none " +
                   "bee_aim_samples=0 bee_aim_error=none bee_aim_within_5deg=none bee_first_fire_distance=none bee_first_hit_distance=none " +
                   "human_aim_samples=0 human_aim_error=none human_aim_within_5deg=none human_first_fire_distance=none human_first_hit_distance=none";
        }

        return $"map_size={FormatDistance(state.EpisodeMapSize)} " +
               $"bee_aim_samples={state.AimSamples[0]} bee_aim_error={FormatAimError(state, 0)} bee_aim_within_5deg={FormatPercent(state.AccurateAimSamples[0], state.AimSamples[0])} " +
               $"bee_first_fire_distance={FormatDistance(state.FirstFireDistance[0])} bee_first_hit_distance={FormatDistance(state.FirstHitDistance[0])} " +
               $"human_aim_samples={state.AimSamples[1]} human_aim_error={FormatAimError(state, 1)} human_aim_within_5deg={FormatPercent(state.AccurateAimSamples[1], state.AimSamples[1])} " +
               $"human_first_fire_distance={FormatDistance(state.FirstFireDistance[1])} human_first_hit_distance={FormatDistance(state.FirstHitDistance[1])}";
    }

    private static long[][] CreateSectorPairCounts()
    {
        long[][] rows = new long[PolicyBearingSectorCount][];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new long[PolicyBearingSectorCount];
        }
        return rows;
    }

    internal static Dictionary<string, object> BuildAimData(Level level, int sideIndex)
    {
        if (!TryGetState(level, out ArenaState state))
        {
            return new Dictionary<string, object>();
        }
        var aim = new Dictionary<string, object>
        {
            ["samples"] = state.AimSamples[sideIndex],
            ["error_sum_deg"] = state.AimErrorDegrees[sideIndex],
            ["within_5deg"] = state.AccurateAimSamples[sideIndex],
            ["first_fire_distance"] = state.FirstFireDistance[sideIndex] < 0f
                ? (object)null : state.FirstFireDistance[sideIndex],
            ["first_hit_distance"] = state.FirstHitDistance[sideIndex] < 0f
                ? (object)null : state.FirstHitDistance[sideIndex]
        };
        // Team 0 trains; team 1 is selected from the self-play opponent pool.
        // Unity has no authoritative checkpoint/version ID for either policy.
        // Do not pretend that a learner publication version identifies the
        // historical self-play checkpoint used for this episode.
        int teamId = state.TeamIds[sideIndex];
        bool movementDisabled = RlOneVsOneTrainingBootstrap.CurrentMovementDisabledForSide(
            level, sideIndex == 0 ? ConfigData.Configuration.BeeSide : ConfigData.Configuration.HumanSide);
        var decisions = new Dictionary<string, object>
        {
            ["team_id"] = teamId,
            ["policy_role"] = teamId == 0 ? "learner" : teamId == 1 ? "self_play_opponent" : "unknown",
            ["policy_version"] = null,
            ["policy_version_source"] = "not_exposed_to_unity",
            ["frame_quarter_turns"] = state.FrameQuarterTurns[sideIndex],
            ["movement_disabled"] = movementDisabled,
            ["policy_decisions"] = state.PolicyDecisions[sideIndex],
            ["with_known_enemy"] = state.PolicyDecisionsWithKnownEnemy[sideIndex],
            ["requested_samples"] = state.RequestedAimSamples[sideIndex],
            ["fresh_aim_samples"] = state.FreshAimSamples[sideIndex],
            ["held_aim_samples"] = state.HeldAimSamples[sideIndex],
            ["requested_error_sum_deg"] = state.RequestedAimErrorDegrees[sideIndex],
            ["requested_within_5deg"] = state.RequestedAimWithin5[sideIndex],
            ["requested_within_15deg"] = state.RequestedAimWithin15[sideIndex],
            ["requested_within_30deg"] = state.RequestedAimWithin30[sideIndex],
            ["target_bearing_sector_counts"] = state.TargetBearingCounts[sideIndex],
            ["requested_bearing_sector_counts"] = state.RequestedBearingCounts[sideIndex],
            ["target_requested_sector_counts"] = state.TargetRequestedSectors[sideIndex],
            ["fresh_target_requested_sector_counts"] = state.FreshTargetRequestedSectors[sideIndex],
            ["held_target_requested_sector_counts"] = state.HeldTargetRequestedSectors[sideIndex],
            ["target_sector_error_sum_deg"] = state.TargetSectorErrorDegrees[sideIndex],
            ["target_sector_within_15deg"] = state.TargetSectorWithin15[sideIndex],
            ["requested_bearing_cos_sum"] = state.RequestedAimCosSum[sideIndex],
            ["requested_bearing_sin_sum"] = state.RequestedAimSinSum[sideIndex]
        };
        aim["policy_decisions"] = decisions;
        // Preserve the preexisting stationary-only field for dashboards reading
        // historical and current episodes side by side.
        if (movementDisabled)
        {
            // The legacy payload remains small: copying the new three 8x8
            // cross-tabs here would double JSONL size on stationary episodes.
            aim["stationary_decisions"] = new Dictionary<string, object>
            {
                ["policy_decisions"] = state.PolicyDecisions[sideIndex],
                ["with_known_enemy"] = state.PolicyDecisionsWithKnownEnemy[sideIndex],
                ["requested_samples"] = state.RequestedAimSamples[sideIndex],
                ["requested_error_sum_deg"] = state.RequestedAimErrorDegrees[sideIndex],
                ["requested_within_5deg"] = state.RequestedAimWithin5[sideIndex],
                ["requested_within_15deg"] = state.RequestedAimWithin15[sideIndex],
                ["requested_within_30deg"] = state.RequestedAimWithin30[sideIndex],
                ["target_bearing_sector_counts"] = state.TargetBearingCounts[sideIndex],
                ["requested_bearing_sector_counts"] = state.RequestedBearingCounts[sideIndex],
                ["target_sector_error_sum_deg"] = state.TargetSectorErrorDegrees[sideIndex],
                ["target_sector_within_15deg"] = state.TargetSectorWithin15[sideIndex],
                ["requested_bearing_cos_sum"] = state.RequestedAimCosSum[sideIndex],
                ["requested_bearing_sin_sum"] = state.RequestedAimSinSum[sideIndex]
            };
        }
        return aim;
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
