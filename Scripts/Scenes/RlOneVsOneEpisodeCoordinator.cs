using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns episode reward bookkeeping and lightweight training diagnostics for the dedicated RL combat scene.
/// The existing Level lifecycle calls the static completion hooks before it tears an episode down,
/// while this component detects each newly spawned battle and captures its starting state/time.
/// </summary>
[DefaultExecutionOrder(-5000)]
internal sealed class RlOneVsOneEpisodeCoordinator : MonoBehaviour
{
    private const int SummaryIntervalEpisodes = 10;

    internal readonly struct EpisodeResult
    {
        internal readonly int EpisodeNumber;
        internal readonly int BeeTeamId;
        internal readonly int HumanTeamId;
        internal readonly int WinningSide;
        internal readonly bool TimedOut;
        internal readonly float DurationSeconds;
        internal readonly int BeeStartingTsv;
        internal readonly int BeeFinalTsv;
        internal readonly int HumanStartingTsv;
        internal readonly int HumanFinalTsv;
        internal readonly int BeeShotsFired;
        internal readonly int BeeShotsHit;
        internal readonly int BeeDamageDealt;
        internal readonly int HumanShotsFired;
        internal readonly int HumanShotsHit;
        internal readonly int HumanDamageDealt;
        internal readonly float BeeTerminalReward;
        internal readonly float BeeTsvReward;
        internal readonly float BeeTimeReward;
        internal readonly float BeeTotalReward;
        internal readonly float HumanTerminalReward;
        internal readonly float HumanTsvReward;
        internal readonly float HumanTimeReward;
        internal readonly float HumanTotalReward;

        internal EpisodeResult(
            int episodeNumber,
            int beeTeamId,
            int humanTeamId,
            int winningSide,
            bool timedOut,
            float durationSeconds,
            int beeStartingTsv,
            int beeFinalTsv,
            int humanStartingTsv,
            int humanFinalTsv,
            int beeShotsFired,
            int beeShotsHit,
            int beeDamageDealt,
            int humanShotsFired,
            int humanShotsHit,
            int humanDamageDealt,
            float beeTerminalReward,
            float beeTsvReward,
            float beeTimeReward,
            float humanTerminalReward,
            float humanTsvReward,
            float humanTimeReward)
        {
            EpisodeNumber = episodeNumber;
            BeeTeamId = beeTeamId;
            HumanTeamId = humanTeamId;
            WinningSide = winningSide;
            TimedOut = timedOut;
            DurationSeconds = durationSeconds;
            BeeStartingTsv = beeStartingTsv;
            BeeFinalTsv = beeFinalTsv;
            HumanStartingTsv = humanStartingTsv;
            HumanFinalTsv = humanFinalTsv;
            BeeShotsFired = beeShotsFired;
            BeeShotsHit = beeShotsHit;
            BeeDamageDealt = beeDamageDealt;
            HumanShotsFired = humanShotsFired;
            HumanShotsHit = humanShotsHit;
            HumanDamageDealt = humanDamageDealt;
            BeeTerminalReward = beeTerminalReward;
            BeeTsvReward = beeTsvReward;
            BeeTimeReward = beeTimeReward;
            BeeTotalReward = beeTerminalReward + beeTsvReward + beeTimeReward;
            HumanTerminalReward = humanTerminalReward;
            HumanTsvReward = humanTsvReward;
            HumanTimeReward = humanTimeReward;
            HumanTotalReward = humanTerminalReward + humanTsvReward + humanTimeReward;
        }
    }

    private readonly struct ShotBaseline
    {
        internal readonly FleetShip FleetShip;
        internal readonly int ShotsAtStart;

        internal ShotBaseline(FleetShip fleetShip)
        {
            FleetShip = fleetShip;
            ShotsAtStart = fleetShip != null ? fleetShip.ShotsFired : 0;
        }
    }

    /// <summary>
    /// Trainer adapters can subscribe to these without putting trainer-specific dependencies into
    /// the battle scene or the core Level lifecycle. Immediate shaping is emitted when the real
    /// outcome/discovery occurs; the episode event carries terminal/time rewards and diagnostics.
    /// </summary>
    internal static event Action<int, float> TsvRewardOccurred;
    internal static event Action<EpisodeResult> EpisodeEnded;

    internal static EpisodeResult LastEpisodeResult { get; private set; }

    private static RlOneVsOneEpisodeCoordinator _active;

    private Stage _stage;
    private Level _level;
    private bool _episodeActive;
    private bool _discoveryRewardsReady;
    private int _episodeNumber;
    private int _beeTeamId;
    private int _humanTeamId;
    private float _episodeStartedAt;
    private int _beeStartingTsv;
    private int _humanStartingTsv;
    private readonly List<ShotBaseline> _beeShotBaselines = new List<ShotBaseline>();
    private readonly List<ShotBaseline> _humanShotBaselines = new List<ShotBaseline>();
    private int _beeHitsThisEpisode;
    private int _humanHitsThisEpisode;
    private int _beeDamageThisEpisode;
    private int _humanDamageThisEpisode;
    private float _beeTsvRewardThisEpisode;
    private float _humanTsvRewardThisEpisode;

    // Discovery denominators are captured before an episode starts rewarding observations. Enemy
    // ships are side-specific; neutral environmental categories have the same denominator for both
    // sides. Collision asteroids are deliberately absent because they can spawn throughout battle.
    private readonly int[] _enemyShipDiscoveryValue = new int[2];
    private readonly int[] _miningAsteroidDiscoveryValue = new int[2];
    private readonly int[] _staticObstacleDiscoveryValue = new int[2];
    private readonly int[] _mapObjectDiscoveryValue = new int[2];
    private readonly int[] _collisionAsteroidDiscoveryCount = new int[2];
    private readonly float[] _rawPositiveShapingReward = new float[2];
    private readonly HashSet<long>[] _rewardedShipDiscoveryIds =
    {
        new HashSet<long>(),
        new HashSet<long>()
    };
    private readonly HashSet<int>[] _rewardedMiningAsteroidDiscoveryIds =
    {
        new HashSet<int>(),
        new HashSet<int>()
    };
    private readonly HashSet<int>[] _rewardedObstacleDiscoveryIds =
    {
        new HashSet<int>(),
        new HashSet<int>()
    };
    private readonly HashSet<int>[] _rewardedMapObjectDiscoveryIds =
    {
        new HashSet<int>(),
        new HashSet<int>()
    };

    private int _completedEpisodes;
    private int _beeWins;
    private int _beeLosses;
    private int _humanWins;
    private int _humanLosses;
    private int _timeouts;
    private long _beeShotsTotal;
    private long _beeHitsTotal;
    private long _beeDamageTotal;
    private long _humanShotsTotal;
    private long _humanHitsTotal;
    private long _humanDamageTotal;
    private float _totalDurationSeconds;

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
            Debug.LogError("RL 1v1 training scene cannot attach its episode coordinator because no Stage exists.");
            return;
        }

        RlOneVsOneEpisodeCoordinator coordinator = stage.GetComponent<RlOneVsOneEpisodeCoordinator>();
        if (coordinator == null)
        {
            coordinator = stage.gameObject.AddComponent<RlOneVsOneEpisodeCoordinator>();
        }
        coordinator._stage = stage;
        _active = coordinator;
    }

    private void OnDestroy()
    {
        if (_active == this)
        {
            _active = null;
        }
    }

    private void Update()
    {
        if (_stage == null || !_stage.IsTrainingNueralNetwork)
        {
            return;
        }

        Level currentLevel = _stage.PrimaryLevel;
        if (currentLevel == null || currentLevel.State == null)
        {
            return;
        }

        if (!_episodeActive || _level != currentLevel)
        {
            TryBeginEpisode(currentLevel);
        }
        if (_episodeActive && !_discoveryRewardsReady)
        {
            TryEnableDiscoveryRewards(currentLevel);
        }
    }

    internal static bool IsControllerForSide(int side, int teamId)
    {
        if (_active == null || !_active._episodeActive || ConfigData.Configuration == null)
        {
            return false;
        }

        if (side == ConfigData.Configuration.BeeSide)
        {
            return teamId == _active._beeTeamId;
        }
        if (side == ConfigData.Configuration.HumanSide)
        {
            return teamId == _active._humanTeamId;
        }
        return false;
    }

    internal static int GetTeamIdForSide(int side, int episodeNumber)
    {
        if (ConfigData.Configuration == null || episodeNumber <= 0)
        {
            return -1;
        }

        int beeTeamId = (episodeNumber & 1) == 1 ? 0 : 1;
        if (side == ConfigData.Configuration.BeeSide)
        {
            return beeTeamId;
        }
        if (side == ConfigData.Configuration.HumanSide)
        {
            return 1 - beeTeamId;
        }
        return -1;
    }

    /// <summary>
    /// Records attributed ship damage after normal damage/TSV calculation has succeeded. Enemy
    /// damage credits the attacker and penalizes the target. Friendly fire penalizes the damaged
    /// side only, so same-side credit can never cancel the casualty-preservation signal.
    /// </summary>
    internal static void RecordHit(Ship attacker, Ship target, int damage, int tsvLoss)
    {
        if (_active == null || !_active._episodeActive || ConfigData.Configuration == null ||
            attacker == null || target == null || attacker.Level != _active._level || target.Level != _active._level)
        {
            return;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        bool attackerIsTrainingSide = attacker.Side == beeSide || attacker.Side == humanSide;
        bool targetIsTrainingSide = target.Side == beeSide || target.Side == humanSide;
        if (!attackerIsTrainingSide || !targetIsTrainingSide)
        {
            return;
        }

        bool isEnemyDamage = attacker.Side != target.Side;
        int appliedDamage = Mathf.Max(0, damage);
        int appliedTsvLoss = Mathf.Max(0, tsvLoss);

        // Hit-rate diagnostics remain enemy-combat diagnostics. Friendly fire still affects reward
        // through TSV loss, but must not make either side appear more accurate in training summaries.
        if (isEnemyDamage)
        {
            if (attacker.Side == beeSide)
            {
                _active._beeHitsThisEpisode++;
                _active._beeDamageThisEpisode += appliedDamage;
            }
            else
            {
                _active._humanHitsThisEpisode++;
                _active._humanDamageThisEpisode += appliedDamage;
            }
        }

        if (appliedTsvLoss <= 0)
        {
            return;
        }

        int combinedStartingTsv = Mathf.Max(1, _active._beeStartingTsv + _active._humanStartingTsv);
        float reward = RlOneVsOneReward.CalculateTsvLossReward(appliedTsvLoss, combinedStartingTsv);
        if (isEnemyDamage)
        {
            _active.ApplyImmediateTsvReward(attacker.Side, reward);
        }
        _active.ApplyImmediateTsvReward(target.Side, -reward);
    }

    /// <summary>
    /// Records TSV lost without an opposing attacker, such as self-damage, collision/environmental
    /// damage, or a self-detonation that uses Ship.LogDamage. This is a casualty-preservation penalty
    /// only; no opposing side receives artificial credit for an unattributed loss.
    /// </summary>
    internal static void RecordUnattributedTsvLoss(Ship target, int tsvLoss)
    {
        if (_active == null || !_active._episodeActive || ConfigData.Configuration == null ||
            target == null || target.Level != _active._level)
        {
            return;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        if (target.Side != beeSide && target.Side != humanSide)
        {
            return;
        }

        int appliedTsvLoss = Mathf.Max(0, tsvLoss);
        if (appliedTsvLoss <= 0)
        {
            return;
        }

        int combinedStartingTsv = Mathf.Max(1, _active._beeStartingTsv + _active._humanStartingTsv);
        float reward = RlOneVsOneReward.CalculateTsvLossReward(appliedTsvLoss, combinedStartingTsv);
        _active.ApplyImmediateTsvReward(target.Side, -reward);
    }

    /// <summary>
    /// Rewards a primitive capability only after it produced a real useful outcome. The action that
    /// attempted it is never rewarded by itself: mining must actually extract minerals, healing must
    /// actually restore TSV, and warping must actually remove a valid ship through a gate.
    /// </summary>
    internal static void RecordSuccessfulCapabilityOutcome(Ship ship, int tsvValue)
    {
        if (_active == null || !_active._episodeActive || ship == null || ship.Level != _active._level)
        {
            return;
        }

        int value = Mathf.Max(0, tsvValue);
        if (value <= 0)
        {
            return;
        }

        int combinedStartingTsv = Mathf.Max(1, _active._beeStartingTsv + _active._humanStartingTsv);
        float reward = RlOneVsOneReward.CalculateTsvLossReward(value, combinedStartingTsv);
        _active.ApplyImmediateTsvReward(ship.Side, reward);
    }

    /// <summary>
    /// Rewards only the first side-wide discovery of an enemy ship. The GameState cache insertion is
    /// the source of truth; this additional ID guard also protects startup races and pooled objects.
    /// </summary>
    internal static void RecordShipDiscovery(Ship observer, Ship spotted)
    {
        if (!TryPrepareDiscovery(observer, out int sideIndex) || spotted == null || spotted.IsDead ||
            spotted.Level != observer.Level || spotted.Side == observer.Side)
        {
            return;
        }

        _active.AwardShipDiscovery(observer.Side, sideIndex, spotted);
    }

    internal static void RecordMiningAsteroidDiscovery(Ship observer, MiningAsteroid asteroid)
    {
        if (!TryPrepareDiscovery(observer, out int sideIndex) || asteroid == null || asteroid.IsDead ||
            asteroid.Level != observer.Level)
        {
            return;
        }

        _active.AwardMiningAsteroidDiscovery(observer.Side, sideIndex, asteroid);
    }

    internal static void RecordMapObjectDiscovery(Ship observer, MapObject mapObject)
    {
        if (!TryPrepareDiscovery(observer, out int sideIndex) || mapObject == null || mapObject.IsDead ||
            mapObject.Level != observer.Level)
        {
            return;
        }

        _active.AwardMapObjectDiscovery(observer.Side, sideIndex, mapObject);
    }

    internal static void RecordObstacleDiscovery(Ship observer, Obstacle obstacle)
    {
        if (!TryPrepareDiscovery(observer, out int sideIndex) || obstacle == null || obstacle.IsDead ||
            obstacle.Level != observer.Level)
        {
            return;
        }

        _active.AwardObstacleDiscovery(observer.Side, sideIndex, obstacle);
    }

    private static bool TryPrepareDiscovery(Ship observer, out int sideIndex)
    {
        sideIndex = -1;
        if (_active == null || ConfigData.Configuration == null || observer == null || observer.IsDead ||
            observer.Level == null)
        {
            return false;
        }

        _active.TryBeginEpisode(observer.Level);
        if (!_active._episodeActive || observer.Level != _active._level || !_active._discoveryRewardsReady)
        {
            return false;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        if (observer.Side != beeSide && observer.Side != humanSide)
        {
            return false;
        }

        sideIndex = observer.Side - 1;
        return sideIndex >= 0 && sideIndex < 2;
    }

    private void AwardShipDiscovery(int side, int sideIndex, Ship spotted)
    {
        if (!_rewardedShipDiscoveryIds[sideIndex].Add(spotted.Id))
        {
            return;
        }

        float reward = RlOneVsOneReward.CalculateStaticDiscoveryReward(
            Mathf.Max(1, spotted.Tsv),
            _enemyShipDiscoveryValue[sideIndex],
            RlOneVsOneReward.EnemyShipDiscoveryBudget);
        ApplyImmediateTsvReward(side, reward);
    }

    private void AwardMiningAsteroidDiscovery(int side, int sideIndex, MiningAsteroid asteroid)
    {
        if (!_rewardedMiningAsteroidDiscoveryIds[sideIndex].Add(asteroid.Id))
        {
            return;
        }

        float reward = RlOneVsOneReward.CalculateStaticDiscoveryReward(
            GetObstacleDiscoveryValue(asteroid),
            _miningAsteroidDiscoveryValue[sideIndex],
            RlOneVsOneReward.MiningAsteroidDiscoveryBudget);
        ApplyImmediateTsvReward(side, reward);
    }

    private void AwardMapObjectDiscovery(int side, int sideIndex, MapObject mapObject)
    {
        if (!_rewardedMapObjectDiscoveryIds[sideIndex].Add(mapObject.Id))
        {
            return;
        }

        float reward = RlOneVsOneReward.CalculateStaticDiscoveryReward(
            GetMapObjectDiscoveryValue(mapObject),
            _mapObjectDiscoveryValue[sideIndex],
            RlOneVsOneReward.MapObjectDiscoveryBudget);
        ApplyImmediateTsvReward(side, reward);
    }

    private void AwardObstacleDiscovery(int side, int sideIndex, Obstacle obstacle)
    {
        if (obstacle is MiningAsteroid miningAsteroid)
        {
            AwardMiningAsteroidDiscovery(side, sideIndex, miningAsteroid);
            return;
        }
        if (obstacle is AsteroidPiece || !_rewardedObstacleDiscoveryIds[sideIndex].Add(obstacle.Id))
        {
            return;
        }

        float reward;
        if (obstacle is CollisionAsteroid collisionAsteroid)
        {
            int discoveryIndex = _collisionAsteroidDiscoveryCount[sideIndex]++;
            reward = RlOneVsOneReward.CalculateCollisionAsteroidDiscoveryReward(
                collisionAsteroid.SizeClass,
                discoveryIndex);
        }
        else
        {
            reward = RlOneVsOneReward.CalculateStaticDiscoveryReward(
                GetObstacleDiscoveryValue(obstacle),
                _staticObstacleDiscoveryValue[sideIndex],
                RlOneVsOneReward.StaticObstacleDiscoveryBudget);
        }

        ApplyImmediateTsvReward(side, reward);
    }

    internal static void CompleteElimination(Level level)
    {
        if (!CanHandle(level))
        {
            return;
        }

        _active.TryBeginEpisode(level);
        if (!_active._episodeActive)
        {
            return;
        }

        int winningSide = DetermineWinner(level);
        _active.CompleteEpisode(level, winningSide, false);
    }

    internal static void CompleteTimeout(Level level)
    {
        if (!CanHandle(level))
        {
            return;
        }

        _active.TryBeginEpisode(level);
        if (_active._episodeActive)
        {
            _active.CompleteEpisode(level, 0, true);
        }
    }

    private static bool CanHandle(Level level)
    {
        return _active != null &&
               level != null &&
               level.Stage != null &&
               RlOneVsOneTrainingBootstrap.IsActiveFor(level.Stage);
    }

    private void TryBeginEpisode(Level level)
    {
        if (level == null || level.State == null || ConfigData.Configuration == null)
        {
            return;
        }

        if (_episodeActive && _level == level)
        {
            return;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        int beeStartingTsv = level.State.InitialTsv[beeSide - 1];
        int humanStartingTsv = level.State.InitialTsv[humanSide - 1];
        List<Ship> beeShips = level.State.GetShips(beeSide);
        List<Ship> humanShips = level.State.GetShips(humanSide);
        int expectedShips = RlOneVsOneTrainingBootstrap.CurrentShipsPerSide;

        if (beeStartingTsv <= 0 || humanStartingTsv <= 0 ||
            CountActiveShips(beeShips) < expectedShips || CountActiveShips(humanShips) < expectedShips ||
            level.State.GameOver)
        {
            return;
        }

        CaptureShotBaselines(beeShips, _beeShotBaselines);
        CaptureShotBaselines(humanShips, _humanShotBaselines);
        if (_beeShotBaselines.Count < expectedShips || _humanShotBaselines.Count < expectedShips)
        {
            return;
        }

        _level = level;
        _episodeNumber++;
        _beeTeamId = GetTeamIdForSide(beeSide, _episodeNumber);
        _humanTeamId = GetTeamIdForSide(humanSide, _episodeNumber);
        _episodeStartedAt = Time.time;
        _beeStartingTsv = beeStartingTsv;
        _humanStartingTsv = humanStartingTsv;
        _beeHitsThisEpisode = 0;
        _humanHitsThisEpisode = 0;
        _beeDamageThisEpisode = 0;
        _humanDamageThisEpisode = 0;
        _beeTsvRewardThisEpisode = 0f;
        _humanTsvRewardThisEpisode = 0f;
        CaptureDiscoveryBaselines(level, beeSide, humanSide);
        _discoveryRewardsReady = false;
        _episodeActive = true;
    }

    private void TryEnableDiscoveryRewards(Level level)
    {
        if (!_episodeActive || _discoveryRewardsReady || level != _level || ConfigData.Configuration == null)
        {
            return;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        if (!ArePolicyControlledShipsReady(level, beeSide) || !ArePolicyControlledShipsReady(level, humanSide))
        {
            return;
        }

        _discoveryRewardsReady = true;

        // Physics callbacks may have populated side-wide caches before all of the active team Agents
        // acquired their ships. Replay that knowledge only after every policy-controlled live ship is
        // bound; the ID guards make the replay and normal cache-insertion path exactly-once.
        RewardExistingDiscoveries(level, beeSide);
        RewardExistingDiscoveries(level, humanSide);
    }

    private static bool ArePolicyControlledShipsReady(Level level, int side)
    {
        List<Ship> ships = level.State.GetShips(side);
        for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
        {
            Ship ship = ships[shipIndex];
            if (RlOneVsOneAgent.RequiresPolicyControl(ship) && !ship.HasBrain)
            {
                return false;
            }
        }
        return true;
    }

    private void CaptureDiscoveryBaselines(Level level, int beeSide, int humanSide)
    {
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            _enemyShipDiscoveryValue[sideIndex] = 0;
            _miningAsteroidDiscoveryValue[sideIndex] = 0;
            _staticObstacleDiscoveryValue[sideIndex] = 0;
            _mapObjectDiscoveryValue[sideIndex] = 0;
            _collisionAsteroidDiscoveryCount[sideIndex] = 0;
            _rawPositiveShapingReward[sideIndex] = 0f;
            _rewardedShipDiscoveryIds[sideIndex].Clear();
            _rewardedMiningAsteroidDiscoveryIds[sideIndex].Clear();
            _rewardedObstacleDiscoveryIds[sideIndex].Clear();
            _rewardedMapObjectDiscoveryIds[sideIndex].Clear();
        }

        int beeSideIndex = beeSide - 1;
        int humanSideIndex = humanSide - 1;
        if (beeSideIndex >= 0 && beeSideIndex < 2 && humanSideIndex >= 0 && humanSideIndex < 2)
        {
            _enemyShipDiscoveryValue[beeSideIndex] = SumShipDiscoveryValue(level.State.GetShips(humanSide));
            _enemyShipDiscoveryValue[humanSideIndex] = SumShipDiscoveryValue(level.State.GetShips(beeSide));
        }

        int miningValue = 0;
        int staticObstacleValue = 0;
        HashSet<Obstacle> countedObstacles = new HashSet<Obstacle>();
        GameObject[] activeObstacleObjects = PathfinderObstacleScope.GetActiveObstacleObjects(level);
        for (int obstacleIndex = 0; obstacleIndex < activeObstacleObjects.Length; obstacleIndex++)
        {
            GameObject obstacleObject = activeObstacleObjects[obstacleIndex];
            Obstacle obstacle = obstacleObject != null ? obstacleObject.GetComponent<Obstacle>() : null;
            if (obstacle == null || obstacle.IsDead || obstacle.Level != level || !countedObstacles.Add(obstacle))
            {
                continue;
            }

            if (obstacle is MiningAsteroid)
            {
                miningValue += GetObstacleDiscoveryValue(obstacle);
            }
            else if (!(obstacle is CollisionAsteroid) && !(obstacle is AsteroidPiece))
            {
                staticObstacleValue += GetObstacleDiscoveryValue(obstacle);
            }
        }

        int mapObjectValue = 0;
        if (level.Map != null && level.Map.Transform != null)
        {
            MapObject[] activeMapObjects = level.Map.Transform.GetComponentsInChildren<MapObject>(false);
            for (int objectIndex = 0; objectIndex < activeMapObjects.Length; objectIndex++)
            {
                MapObject mapObject = activeMapObjects[objectIndex];
                if (mapObject != null && !mapObject.IsDead && mapObject.Level == level)
                {
                    mapObjectValue += GetMapObjectDiscoveryValue(mapObject);
                }
            }
        }

        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            _miningAsteroidDiscoveryValue[sideIndex] = miningValue;
            _staticObstacleDiscoveryValue[sideIndex] = staticObstacleValue;
            _mapObjectDiscoveryValue[sideIndex] = mapObjectValue;
        }
    }

    private void RewardExistingDiscoveries(Level level, int side)
    {
        int sideIndex = side - 1;
        if (sideIndex < 0 || sideIndex >= 2)
        {
            return;
        }

        foreach (Ship spotted in level.State.VisionCache[sideIndex])
        {
            if (spotted != null && !spotted.IsDead && spotted.Side != side)
            {
                AwardShipDiscovery(side, sideIndex, spotted);
            }
        }
        foreach (MiningAsteroid asteroid in level.State.HiveMindMiningAsteroidCache[sideIndex])
        {
            if (asteroid != null && !asteroid.IsDead)
            {
                AwardMiningAsteroidDiscovery(side, sideIndex, asteroid);
            }
        }
        foreach (Obstacle obstacle in level.State.HiveMindObstacleCache[sideIndex])
        {
            if (obstacle != null && !obstacle.IsDead)
            {
                AwardObstacleDiscovery(side, sideIndex, obstacle);
            }
        }
        foreach (MapObject mapObject in level.State.HiveMindMapObjectCache[sideIndex])
        {
            if (mapObject != null && !mapObject.IsDead)
            {
                AwardMapObjectDiscovery(side, sideIndex, mapObject);
            }
        }
    }

    private static int SumShipDiscoveryValue(List<Ship> ships)
    {
        int total = 0;
        for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
        {
            Ship ship = ships[shipIndex];
            if (ship != null && !ship.IsDead)
            {
                total += Mathf.Max(1, ship.Tsv);
            }
        }
        return total;
    }

    private static int GetObstacleDiscoveryValue(Obstacle obstacle)
    {
        return Mathf.Max(1, obstacle.OriginalHealth > 0 ? obstacle.OriginalHealth : obstacle.Health);
    }

    private static int GetMapObjectDiscoveryValue(MapObject mapObject)
    {
        return Mathf.Max(1, mapObject.MaxHealth > 0 ? mapObject.MaxHealth : mapObject.Health);
    }

    private void ApplyImmediateTsvReward(int side, float reward)
    {
        int sideIndex = side - 1;
        if (sideIndex < 0 || sideIndex >= _rawPositiveShapingReward.Length)
        {
            return;
        }

        float emittedReward = reward;
        if (reward > 0f)
        {
            float previousBounded = RlOneVsOneReward.CalculateBoundedPositiveShapingReward(
                _rawPositiveShapingReward[sideIndex]);
            _rawPositiveShapingReward[sideIndex] += reward;
            float nextBounded = RlOneVsOneReward.CalculateBoundedPositiveShapingReward(
                _rawPositiveShapingReward[sideIndex]);
            emittedReward = Mathf.Max(0f, nextBounded - previousBounded);
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        if (side == beeSide)
        {
            _beeTsvRewardThisEpisode += emittedReward;
        }
        else if (side == humanSide)
        {
            _humanTsvRewardThisEpisode += emittedReward;
        }
        else
        {
            return;
        }

        TsvRewardOccurred?.Invoke(side, emittedReward);
    }

    private void CompleteEpisode(Level level, int winningSide, bool timedOut)
    {
        if (!_episodeActive || level != _level)
        {
            return;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        int beeFinalTsv = level.State.GetTsvBySide(beeSide);
        int humanFinalTsv = level.State.GetTsvBySide(humanSide);
        int beeShotsFired = CalculateShotsFired(_beeShotBaselines);
        int humanShotsFired = CalculateShotsFired(_humanShotBaselines);
        float durationSeconds = Mathf.Clamp(
            Time.time - _episodeStartedAt,
            0f,
            RlOneVsOneTrainingBootstrap.CurrentTimeoutSeconds);

        float beeTerminal = RlOneVsOneReward.CalculateTerminalReward(beeSide, winningSide);
        float humanTerminal = RlOneVsOneReward.CalculateTerminalReward(humanSide, winningSide);

        float beeTimeReward = winningSide == beeSide
            ? RlOneVsOneReward.CalculateTimePenalty(durationSeconds)
            : 0f;
        float humanTimeReward = winningSide == humanSide
            ? RlOneVsOneReward.CalculateTimePenalty(durationSeconds)
            : 0f;

        LastEpisodeResult = new EpisodeResult(
            _episodeNumber,
            _beeTeamId,
            _humanTeamId,
            winningSide,
            timedOut,
            durationSeconds,
            _beeStartingTsv,
            beeFinalTsv,
            _humanStartingTsv,
            humanFinalTsv,
            beeShotsFired,
            _beeHitsThisEpisode,
            _beeDamageThisEpisode,
            humanShotsFired,
            _humanHitsThisEpisode,
            _humanDamageThisEpisode,
            beeTerminal,
            _beeTsvRewardThisEpisode,
            beeTimeReward,
            humanTerminal,
            _humanTsvRewardThisEpisode,
            humanTimeReward);

        UpdateRunningDiagnostics(LastEpisodeResult, beeSide, humanSide);

        Debug.Log(
            $"RL 1v1 episode={LastEpisodeResult.EpisodeNumber} bee_team={_beeTeamId} human_team={_humanTeamId} " +
            $"ships_per_side={RlOneVsOneTrainingBootstrap.CurrentShipsPerSide} " +
            $"winner={winningSide} timeout={timedOut} duration={durationSeconds:F2}s " +
            $"bee_tsv={_beeStartingTsv}->{beeFinalTsv} human_tsv={_humanStartingTsv}->{humanFinalTsv} " +
            $"bee_shots={beeShotsFired} bee_hits={_beeHitsThisEpisode} bee_damage={_beeDamageThisEpisode} " +
            $"bee_tsv_reward={_beeTsvRewardThisEpisode:F4} " +
            $"human_shots={humanShotsFired} human_hits={_humanHitsThisEpisode} human_damage={_humanDamageThisEpisode} " +
            $"human_tsv_reward={_humanTsvRewardThisEpisode:F4} " +
            $"bee_reward={LastEpisodeResult.BeeTotalReward:F4} " +
            $"human_reward={LastEpisodeResult.HumanTotalReward:F4}");

        if (_completedEpisodes % SummaryIntervalEpisodes == 0)
        {
            LogRunningSummary();
        }

        _episodeActive = false;
        _discoveryRewardsReady = false;
        EpisodeEnded?.Invoke(LastEpisodeResult);
    }

    private void UpdateRunningDiagnostics(EpisodeResult result, int beeSide, int humanSide)
    {
        _completedEpisodes++;
        _totalDurationSeconds += result.DurationSeconds;
        _beeShotsTotal += result.BeeShotsFired;
        _beeHitsTotal += result.BeeShotsHit;
        _beeDamageTotal += result.BeeDamageDealt;
        _humanShotsTotal += result.HumanShotsFired;
        _humanHitsTotal += result.HumanShotsHit;
        _humanDamageTotal += result.HumanDamageDealt;

        if (result.TimedOut || result.WinningSide == 0)
        {
            _beeLosses++;
            _humanLosses++;
            if (result.TimedOut)
            {
                _timeouts++;
            }
        }
        else if (result.WinningSide == beeSide)
        {
            _beeWins++;
            _humanLosses++;
        }
        else if (result.WinningSide == humanSide)
        {
            _humanWins++;
            _beeLosses++;
        }
    }

    private void LogRunningSummary()
    {
        float averageDuration = _completedEpisodes > 0 ? _totalDurationSeconds / _completedEpisodes : 0f;
        float beeHitRate = _beeShotsTotal > 0 ? (float)_beeHitsTotal / _beeShotsTotal : 0f;
        float humanHitRate = _humanShotsTotal > 0 ? (float)_humanHitsTotal / _humanShotsTotal : 0f;

        Debug.Log(
            $"RL 1v1 summary episodes={_completedEpisodes} " +
            $"bee_record={_beeWins}-{_beeLosses} human_record={_humanWins}-{_humanLosses} " +
            $"timeouts={_timeouts} avg_duration={averageDuration:F2}s " +
            $"bee_shots={_beeShotsTotal} bee_hits={_beeHitsTotal} bee_hit_rate={beeHitRate:P2} bee_damage={_beeDamageTotal} " +
            $"human_shots={_humanShotsTotal} human_hits={_humanHitsTotal} human_hit_rate={humanHitRate:P2} human_damage={_humanDamageTotal}");
    }

    private static int CountActiveShips(List<Ship> ships)
    {
        int count = 0;
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship != null && !ship.IsDead && ship.FleetShip != null)
            {
                count++;
            }
        }
        return count;
    }

    private static void CaptureShotBaselines(List<Ship> ships, List<ShotBaseline> destination)
    {
        destination.Clear();
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship != null && !ship.IsDead && ship.FleetShip != null)
            {
                destination.Add(new ShotBaseline(ship.FleetShip));
            }
        }
    }

    private static int CalculateShotsFired(List<ShotBaseline> baselines)
    {
        int shots = 0;
        for (int i = 0; i < baselines.Count; i++)
        {
            ShotBaseline baseline = baselines[i];
            int currentShots = baseline.FleetShip != null ? baseline.FleetShip.ShotsFired : baseline.ShotsAtStart;
            shots += Mathf.Max(0, currentShots - baseline.ShotsAtStart);
        }
        return shots;
    }

    private static int DetermineWinner(Level level)
    {
        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        bool beesKilled = level.State.IsSideKilled(beeSide);
        bool humansKilled = level.State.IsSideKilled(humanSide);

        if (beesKilled == humansKilled)
        {
            return 0;
        }
        return beesKilled ? humanSide : beeSide;
    }
}
