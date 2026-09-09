using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Training-only combat diagnostics that do not participate in reward calculation. The component
/// samples requested turret aim against the best-aligned live enemy and detects the first actual
/// projectile launch / first recorded enemy hit from the normal FleetShip and Ship combat state.
/// </summary>
[DefaultExecutionOrder(6000)]
internal sealed class RlOneVsOneCombatTelemetry : MonoBehaviour
{
    private const float AccurateAimThresholdDegrees = 5f;

    private Stage _stage;
    private Level _level;
    private float _episodeMapSize = -1f;

    private readonly Dictionary<long, int> _shotBaselines = new Dictionary<long, int>();
    private readonly Dictionary<long, HashSet<long>> _hitBaselines = new Dictionary<long, HashSet<long>>();
    private readonly double[] _aimErrorDegrees = new double[2];
    private readonly long[] _aimSamples = new long[2];
    private readonly long[] _accurateAimSamples = new long[2];
    private readonly long[] _alignedTurretSamples = new long[2];
    private readonly float[] _firstFireDistance = { -1f, -1f };
    private readonly float[] _firstHitDistance = { -1f, -1f };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AttachToDedicatedTrainingScene()
    {
        if (!RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            return;
        }

        RlOneVsOneCombatTelemetry telemetry = stage.GetComponent<RlOneVsOneCombatTelemetry>();
        if (telemetry == null)
        {
            telemetry = stage.gameObject.AddComponent<RlOneVsOneCombatTelemetry>();
        }
        telemetry.Configure(stage);
    }

    private void Configure(Stage stage)
    {
        if (_stage != null)
        {
            RlOneVsOneEpisodeCoordinator.EpisodeEnded -= HandleEpisodeEnded;
        }
        _stage = stage;
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
    }

    private void OnDestroy()
    {
        RlOneVsOneEpisodeCoordinator.EpisodeEnded -= HandleEpisodeEnded;
    }

    private void Update()
    {
        if (_stage == null || !RlOneVsOneTrainingBootstrap.IsActiveFor(_stage) || ConfigData.Configuration == null)
        {
            return;
        }

        Level currentLevel = _stage.PrimaryLevel;
        if (currentLevel == null || currentLevel.State == null)
        {
            return;
        }

        if (_level != currentLevel)
        {
            BeginTracking(currentLevel);
        }

        TrackSide(currentLevel, ConfigData.Configuration.BeeSide, 0);
        TrackSide(currentLevel, ConfigData.Configuration.HumanSide, 1);
    }

    private void BeginTracking(Level level)
    {
        _level = level;
        _episodeMapSize = RlOneVsOneTrainingBootstrap.CurrentMapSize;
        ResetEpisodeMetrics();
        CaptureSideBaselines(level, ConfigData.Configuration.BeeSide);
        CaptureSideBaselines(level, ConfigData.Configuration.HumanSide);
    }

    private void CaptureSideBaselines(Level level, int side)
    {
        List<Ship> ships = level.State.GetShips(side);
        for (int i = 0; i < ships.Count; i++)
        {
            EnsureShipBaseline(ships[i]);
        }
    }

    private void EnsureShipBaseline(Ship ship)
    {
        if (ship == null || ship.FleetShip == null || _shotBaselines.ContainsKey(ship.Id))
        {
            return;
        }

        _shotBaselines[ship.Id] = ship.FleetShip.ShotsFired;
        HashSet<long> targets = new HashSet<long>();
        foreach (Ship target in ship.ShipsHit)
        {
            if (target != null)
            {
                targets.Add(target.Id);
            }
        }
        _hitBaselines[ship.Id] = targets;
    }

    private void TrackSide(Level level, int side, int sideIndex)
    {
        List<Ship> ships = level.State.GetShips(side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship == null || ship.IsDead || ship.FleetShip == null)
            {
                continue;
            }

            EnsureShipBaseline(ship);
            TrackAimSamples(level, ship, sideIndex);
            TrackFirstFire(level, ship, sideIndex);
            TrackFirstHit(ship, sideIndex);
        }
    }

    private void TrackAimSamples(Level level, Ship ship, int sideIndex)
    {
        for (int weaponIndex = 0; weaponIndex < ship.Weapons.Count; weaponIndex++)
        {
            Turret turret = ship.Weapons[weaponIndex] as Turret;
            if (turret == null || !turret.IsRlControlled || !turret.RlFireRequested)
            {
                continue;
            }

            float distance;
            float error;
            if (!TryFindBestAimedEnemy(level, ship.Side, turret.GetPosition(), turret.RlTargetPoint, out distance, out error))
            {
                continue;
            }

            _aimSamples[sideIndex]++;
            _aimErrorDegrees[sideIndex] += error;
            if (error <= AccurateAimThresholdDegrees)
            {
                _accurateAimSamples[sideIndex]++;
            }
            if (turret.IsAimedAtTarget)
            {
                _alignedTurretSamples[sideIndex]++;
            }
        }
    }

    private void TrackFirstFire(Level level, Ship ship, int sideIndex)
    {
        if (_firstFireDistance[sideIndex] >= 0f)
        {
            return;
        }

        int baseline;
        if (!_shotBaselines.TryGetValue(ship.Id, out baseline) || ship.FleetShip.ShotsFired <= baseline)
        {
            return;
        }

        float bestDistance = -1f;
        float bestError = float.MaxValue;
        for (int weaponIndex = 0; weaponIndex < ship.Weapons.Count; weaponIndex++)
        {
            Turret turret = ship.Weapons[weaponIndex] as Turret;
            if (turret == null || !turret.IsRlControlled)
            {
                continue;
            }

            float distance;
            float error;
            if (TryFindBestAimedEnemy(level, ship.Side, turret.GetPosition(), turret.RlTargetPoint, out distance, out error) &&
                error < bestError)
            {
                bestError = error;
                bestDistance = distance;
            }
        }

        if (bestDistance < 0f)
        {
            bestDistance = FindNearestEnemyDistance(level, ship);
        }
        _firstFireDistance[sideIndex] = bestDistance;
    }

    private void TrackFirstHit(Ship ship, int sideIndex)
    {
        if (_firstHitDistance[sideIndex] >= 0f)
        {
            return;
        }

        HashSet<long> baseline;
        if (!_hitBaselines.TryGetValue(ship.Id, out baseline))
        {
            return;
        }

        Ship firstNewTarget = null;
        float nearestDistance = float.MaxValue;
        foreach (Ship target in ship.ShipsHit)
        {
            if (target == null || target.Side == ship.Side || baseline.Contains(target.Id))
            {
                continue;
            }

            float distance = Vector2.Distance(ship.GetPosition(), target.GetPosition());
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                firstNewTarget = target;
            }
        }

        if (firstNewTarget != null)
        {
            _firstHitDistance[sideIndex] = nearestDistance;
        }
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
        if (level == null || level.State == null || ConfigData.Configuration == null)
        {
            return false;
        }

        Vector2 aimDirection = aimPoint - origin;
        if (aimDirection.sqrMagnitude <= 0.000001f)
        {
            return false;
        }

        int enemySide = firingSide == ConfigData.Configuration.BeeSide
            ? ConfigData.Configuration.HumanSide
            : ConfigData.Configuration.BeeSide;
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
        int enemySide = ship.Side == ConfigData.Configuration.BeeSide
            ? ConfigData.Configuration.HumanSide
            : ConfigData.Configuration.BeeSide;
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

    private void HandleEpisodeEnded(RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        Debug.Log(
            $"RL 1v1 telemetry episode={result.EpisodeNumber} map_size={FormatDistance(_episodeMapSize)} " +
            $"bee_aim_samples={_aimSamples[0]} bee_aim_error={FormatAimError(0)} bee_aim_within_5deg={FormatPercent(_accurateAimSamples[0], _aimSamples[0])} " +
            $"bee_turret_aligned={FormatPercent(_alignedTurretSamples[0], _aimSamples[0])} bee_first_fire_distance={FormatDistance(_firstFireDistance[0])} bee_first_hit_distance={FormatDistance(_firstHitDistance[0])} " +
            $"human_aim_samples={_aimSamples[1]} human_aim_error={FormatAimError(1)} human_aim_within_5deg={FormatPercent(_accurateAimSamples[1], _aimSamples[1])} " +
            $"human_turret_aligned={FormatPercent(_alignedTurretSamples[1], _aimSamples[1])} human_first_fire_distance={FormatDistance(_firstFireDistance[1])} human_first_hit_distance={FormatDistance(_firstHitDistance[1])}");

        _level = null;
        _episodeMapSize = -1f;
        ResetEpisodeMetrics();
    }

    private void ResetEpisodeMetrics()
    {
        _shotBaselines.Clear();
        _hitBaselines.Clear();
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            _aimErrorDegrees[sideIndex] = 0d;
            _aimSamples[sideIndex] = 0;
            _accurateAimSamples[sideIndex] = 0;
            _alignedTurretSamples[sideIndex] = 0;
            _firstFireDistance[sideIndex] = -1f;
            _firstHitDistance[sideIndex] = -1f;
        }
    }

    private string FormatAimError(int sideIndex)
    {
        return _aimSamples[sideIndex] == 0
            ? "none"
            : $"{(_aimErrorDegrees[sideIndex] / _aimSamples[sideIndex]):F2}deg";
    }

    private static string FormatPercent(long numerator, long denominator)
    {
        return denominator <= 0 ? "none" : $"{((double)numerator / denominator):P2}";
    }

    private static string FormatDistance(float value)
    {
        return value < 0f ? "none" : $"{value:F2}";
    }
}