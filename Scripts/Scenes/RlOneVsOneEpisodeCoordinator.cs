using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
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
    private int _beeShotsThisEpisode;
    private int _humanShotsThisEpisode;
    private int _beeFireRequestsThisEpisode;
    private int _humanFireRequestsThisEpisode;
    private int _beeHitsThisEpisode;
    private int _humanHitsThisEpisode;
    private int _beeDamageThisEpisode;
    private int _humanDamageThisEpisode;
    private float _beeTsvRewardThisEpisode;
    private float _humanTsvRewardThisEpisode;
    private float _beeFirstContactSeconds;
    private float _humanFirstContactSeconds;
    private float _beeFirstFireSeconds;
    private float _humanFirstFireSeconds;
    private float _beeFirstHitSeconds;
    private float _humanFirstHitSeconds;

    private readonly HashSet<long>[] _initialShipIds = { new HashSet<long>(), new HashSet<long>() };
    private readonly HashSet<long>[] _seenShipIds = { new HashSet<long>(), new HashSet<long>() };
    private readonly HashSet<long>[] _policyEligibleShipIds = { new HashSet<long>(), new HashSet<long>() };
    private readonly HashSet<long>[] _policyControlledShipIds = { new HashSet<long>(), new HashSet<long>() };
    private readonly Dictionary<ConfigData.WeaponTypes, int>[] _fireRequestsByWeapon =
    {
        new Dictionary<ConfigData.WeaponTypes, int>(),
        new Dictionary<ConfigData.WeaponTypes, int>()
    };
    private readonly Dictionary<ConfigData.WeaponTypes, int>[] _shotsByWeapon =
    {
        new Dictionary<ConfigData.WeaponTypes, int>(),
        new Dictionary<ConfigData.WeaponTypes, int>()
    };

    // Discovery denominators are captured before an episode starts rewarding observations. Enemy
    // ships are side-specific; neutral environmental categories have the same denominator for both
    // sides. Collision asteroids are deliberately absent because they can spawn throughout battle.
    private readonly int[] _enemyShipDiscoveryValue = new int[2];
    private readonly int[] _miningAsteroidDiscoveryValue = new int[2];
    private readonly int[] _staticObstacleDiscoveryValue = new int[2];
    private readonly int[] _mapObjectDiscoveryValue = new int[2];
    private readonly int[] _collisionAsteroidDiscoveryCount = new int[2];
    private readonly double[] _rawPositiveShapingReward = new double[2];
    private readonly HashSet<long>[] _rewardedShipDiscoveryIds = { new HashSet<long>(), new HashSet<long>() };
    private readonly HashSet<int>[] _rewardedMiningAsteroidDiscoveryIds = { new HashSet<int>(), new HashSet<int>() };
    private readonly HashSet<int>[] _rewardedObstacleDiscoveryIds = { new HashSet<int>(), new HashSet<int>() };
    private readonly HashSet<int>[] _rewardedMapObjectDiscoveryIds = { new HashSet<int>(), new HashSet<int>() };

    private int _completedEpisodes;
    private int _beeWins;
    private int _beeLosses;
    private int _humanWins;
    private int _humanLosses;
    private int _draws;
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
        if (_episodeActive)
        {
            TrackEpisodeShips(currentLevel);
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

    internal static void RecordFireRequest(Ship ship, Weapon weapon)
    {
        if (!TryGetTrackedSide(ship, out int sideIndex))
        {
            return;
        }

        if (sideIndex == 0)
        {
            _active._beeFireRequestsThisEpisode++;
        }
        else
        {
            _active._humanFireRequestsThisEpisode++;
        }
        IncrementWeaponCount(_active._fireRequestsByWeapon[sideIndex], weapon);
    }

    internal static void RecordShotFired(Ship ship, Weapon weapon)
    {
        if (!TryGetTrackedSide(ship, out int sideIndex))
        {
            return;
        }

        if (sideIndex == 0)
        {
            _active._beeShotsThisEpisode++;
            if (_active._beeFirstFireSeconds < 0f)
            {
                _active._beeFirstFireSeconds = _active.ElapsedEpisodeSeconds;
            }
        }
        else
        {
            _active._humanShotsThisEpisode++;
            if (_active._humanFirstFireSeconds < 0f)
            {
                _active._humanFirstFireSeconds = _active.ElapsedEpisodeSeconds;
            }
        }
        IncrementWeaponCount(_active._shotsByWeapon[sideIndex], weapon);
    }

    private static bool TryGetTrackedSide(Ship ship, out int sideIndex)
    {
        sideIndex = -1;
        if (_active == null || !_active._episodeActive || ConfigData.Configuration == null ||
            ship == null || ship.Level != _active._level)
        {
            return false;
        }

        if (ship.Side == ConfigData.Configuration.BeeSide)
        {
            sideIndex = 0;
            return true;
        }
        if (ship.Side == ConfigData.Configuration.HumanSide)
        {
            sideIndex = 1;
            return true;
        }
        return false;
    }

    private static void IncrementWeaponCount(Dictionary<ConfigData.WeaponTypes, int> counts, Weapon weapon)
    {
        if (weapon == null)
        {
            return;
        }
        counts.TryGetValue(weapon.Type, out int current);
        counts[weapon.Type] = current + 1;
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

        if (isEnemyDamage && appliedDamage > 0)
        {
            if (attacker.Side == beeSide)
            {
                _active._beeHitsThisEpisode++;
                _active._beeDamageThisEpisode += appliedDamage;
                if (_active._beeFirstHitSeconds < 0f)
                {
                    _active._beeFirstHitSeconds = _active.ElapsedEpisodeSeconds;
                }
            }
            else
            {
                _active._humanHitsThisEpisode++;
                _active._humanDamageThisEpisode += appliedDamage;
                if (_active._humanFirstHitSeconds < 0f)
                {
                    _active._humanFirstHitSeconds = _active.ElapsedEpisodeSeconds;
                }
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

        sideIndex = observer.Side == beeSide ? 0 : 1;
        return true;
    }

    private void AwardShipDiscovery(int side, int sideIndex, Ship spotted)
    {
        if (!_rewardedShipDiscoveryIds[sideIndex].Add(spotted.Id))
        {
            return;
        }

        if (sideIndex == 0 && _beeFirstContactSeconds < 0f)
        {
            _beeFirstContactSeconds = ElapsedEpisodeSeconds;
        }
        else if (sideIndex == 1 && _humanFirstContactSeconds < 0f)
        {
            _humanFirstContactSeconds = ElapsedEpisodeSeconds;
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
            reward = RlOneVsOneReward.CalculateCollisionAsteroidDiscoveryReward(collisionAsteroid.SizeClass, discoveryIndex);
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
        if (_active._episodeActive)
        {
            _active.CompleteEpisode(level, DetermineWinner(level), false);
        }
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
        return _active != null && level != null && level.Stage != null &&
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

        _level = level;
        _episodeNumber++;
        _beeTeamId = GetTeamIdForSide(beeSide, _episodeNumber);
        _humanTeamId = GetTeamIdForSide(humanSide, _episodeNumber);
        _episodeStartedAt = Time.time;
        _beeStartingTsv = beeStartingTsv;
        _humanStartingTsv = humanStartingTsv;
        _beeShotsThisEpisode = 0;
        _humanShotsThisEpisode = 0;
        _beeFireRequestsThisEpisode = 0;
        _humanFireRequestsThisEpisode = 0;
        _beeHitsThisEpisode = 0;
        _humanHitsThisEpisode = 0;
        _beeDamageThisEpisode = 0;
        _humanDamageThisEpisode = 0;
        _beeTsvRewardThisEpisode = 0f;
        _humanTsvRewardThisEpisode = 0f;
        _beeFirstContactSeconds = -1f;
        _humanFirstContactSeconds = -1f;
        _beeFirstFireSeconds = -1f;
        _humanFirstFireSeconds = -1f;
        _beeFirstHitSeconds = -1f;
        _humanFirstHitSeconds = -1f;
        ResetShipDiagnostics(beeShips, humanShips);
        CaptureDiscoveryBaselines(level, beeSide, humanSide);
        _discoveryRewardsReady = false;
        _episodeActive = true;
        TrackEpisodeShips(level);
    }

    private void ResetShipDiagnostics(List<Ship> beeShips, List<Ship> humanShips)
    {
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            _initialShipIds[sideIndex].Clear();
            _seenShipIds[sideIndex].Clear();
            _policyEligibleShipIds[sideIndex].Clear();
            _policyControlledShipIds[sideIndex].Clear();
            _fireRequestsByWeapon[sideIndex].Clear();
            _shotsByWeapon[sideIndex].Clear();
        }
        AddInitialShipIds(beeShips, _initialShipIds[0]);
        AddInitialShipIds(humanShips, _initialShipIds[1]);
    }

    private static void AddInitialShipIds(List<Ship> ships, HashSet<long> destination)
    {
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship != null && !ship.IsDead)
            {
                destination.Add(ship.Id);
            }
        }
    }

    private void TrackEpisodeShips(Level level)
    {
        if (ConfigData.Configuration == null)
        {
            return;
        }
        TrackSideShips(level.State.GetShips(ConfigData.Configuration.BeeSide), 0);
        TrackSideShips(level.State.GetShips(ConfigData.Configuration.HumanSide), 1);
    }

    private void TrackSideShips(List<Ship> ships, int sideIndex)
    {
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship == null)
            {
                continue;
            }
            _seenShipIds[sideIndex].Add(ship.Id);
            if (RlOneVsOneAgent.RequiresPolicyControl(ship))
            {
                _policyEligibleShipIds[sideIndex].Add(ship.Id);
                if (ship.HasBrain)
                {
                    _policyControlledShipIds[sideIndex].Add(ship.Id);
                }
            }
        }
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
            _rawPositiveShapingReward[sideIndex] = 0d;
            _rewardedShipDiscoveryIds[sideIndex].Clear();
            _rewardedMiningAsteroidDiscoveryIds[sideIndex].Clear();
            _rewardedObstacleDiscoveryIds[sideIndex].Clear();
            _rewardedMapObjectDiscoveryIds[sideIndex].Clear();
        }

        _enemyShipDiscoveryValue[0] = SumShipDiscoveryValue(level.State.GetShips(humanSide));
        _enemyShipDiscoveryValue[1] = SumShipDiscoveryValue(level.State.GetShips(beeSide));

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
        int sideIndex = side == ConfigData.Configuration.BeeSide ? 0 : 1;
        int cacheIndex = side - 1;
        foreach (Ship spotted in level.State.VisionCache[cacheIndex])
        {
            if (spotted != null && !spotted.IsDead && spotted.Side != side)
            {
                AwardShipDiscovery(side, sideIndex, spotted);
            }
        }
        foreach (MiningAsteroid asteroid in level.State.HiveMindMiningAsteroidCache[cacheIndex])
        {
            if (asteroid != null && !asteroid.IsDead)
            {
                AwardMiningAsteroidDiscovery(side, sideIndex, asteroid);
            }
        }
        foreach (Obstacle obstacle in level.State.HiveMindObstacleCache[cacheIndex])
        {
            if (obstacle != null && !obstacle.IsDead)
            {
                AwardObstacleDiscovery(side, sideIndex, obstacle);
            }
        }
        foreach (MapObject mapObject in level.State.HiveMindMapObjectCache[cacheIndex])
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
        int sideIndex = side == ConfigData.Configuration.BeeSide ? 0 :
            side == ConfigData.Configuration.HumanSide ? 1 : -1;
        if (sideIndex < 0)
        {
            return;
        }

        float emittedReward = reward;
        if (reward > 0f)
        {
            double rawBefore = _rawPositiveShapingReward[sideIndex];
            double boundedIncrement = RlOneVsOneReward.CalculateBoundedPositiveShapingIncrement(rawBefore, reward);
            _rawPositiveShapingReward[sideIndex] = rawBefore + reward;
            emittedReward = (float)Math.Max(0d, boundedIncrement);
        }

        if (sideIndex == 0)
        {
            _beeTsvRewardThisEpisode += emittedReward;
        }
        else
        {
            _humanTsvRewardThisEpisode += emittedReward;
        }
        TsvRewardOccurred?.Invoke(side, emittedReward);
    }

    private void CompleteEpisode(Level level, int winningSide, bool timedOut)
    {
        if (!_episodeActive || level != _level)
        {
            return;
        }

        TrackEpisodeShips(level);
        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        int beeFinalTsv = level.State.GetTsvBySide(beeSide);
        int humanFinalTsv = level.State.GetTsvBySide(humanSide);
        float durationSeconds = Mathf.Clamp(ElapsedEpisodeSeconds, 0f, RlOneVsOneTrainingBootstrap.CurrentTimeoutSeconds);

        float beeTerminal = RlOneVsOneReward.CalculateTerminalReward(beeSide, winningSide, timedOut);
        float humanTerminal = RlOneVsOneReward.CalculateTerminalReward(humanSide, winningSide, timedOut);
        float beeTimeReward = winningSide == beeSide ? RlOneVsOneReward.CalculateTimePenalty(durationSeconds) : 0f;
        float humanTimeReward = winningSide == humanSide ? RlOneVsOneReward.CalculateTimePenalty(durationSeconds) : 0f;

        LastEpisodeResult = new EpisodeResult(
            _episodeNumber, _beeTeamId, _humanTeamId, winningSide, timedOut, durationSeconds,
            _beeStartingTsv, beeFinalTsv, _humanStartingTsv, humanFinalTsv,
            _beeShotsThisEpisode, _beeHitsThisEpisode, _beeDamageThisEpisode,
            _humanShotsThisEpisode, _humanHitsThisEpisode, _humanDamageThisEpisode,
            beeTerminal, _beeTsvRewardThisEpisode, beeTimeReward,
            humanTerminal, _humanTsvRewardThisEpisode, humanTimeReward);

        UpdateRunningDiagnostics(LastEpisodeResult, beeSide, humanSide);

        string outcome = timedOut ? "timeout" : winningSide == 0 ? "draw" : $"side_{winningSide}_win";
        int beeSpawned = CountSpawnedShips(0);
        int humanSpawned = CountSpawnedShips(1);
        Debug.Log(
            $"RL 1v1 episode={LastEpisodeResult.EpisodeNumber} outcome={outcome} bee_team={_beeTeamId} human_team={_humanTeamId} " +
            $"ships_per_side={RlOneVsOneTrainingBootstrap.CurrentShipsPerSide} winner={winningSide} timeout={timedOut} duration={durationSeconds:F2}s " +
            $"bee_tsv={_beeStartingTsv}->{beeFinalTsv} human_tsv={_humanStartingTsv}->{humanFinalTsv} " +
            $"bee_fire_requests={_beeFireRequestsThisEpisode} bee_shots={_beeShotsThisEpisode} bee_hits={_beeHitsThisEpisode} bee_damage={_beeDamageThisEpisode} " +
            $"bee_first_contact={FormatTime(_beeFirstContactSeconds)} bee_first_fire={FormatTime(_beeFirstFireSeconds)} bee_first_hit={FormatTime(_beeFirstHitSeconds)} " +
            $"bee_spawned={beeSpawned} bee_agent_coverage={_policyControlledShipIds[0].Count}/{_policyEligibleShipIds[0].Count} " +
            $"bee_weapons={FormatWeaponActivity(0)} " +
            $"bee_rewards=terminal:{beeTerminal:F4},tsv:{_beeTsvRewardThisEpisode:F4},time:{beeTimeReward:F4},total:{LastEpisodeResult.BeeTotalReward:F4} " +
            $"human_fire_requests={_humanFireRequestsThisEpisode} human_shots={_humanShotsThisEpisode} human_hits={_humanHitsThisEpisode} human_damage={_humanDamageThisEpisode} " +
            $"human_first_contact={FormatTime(_humanFirstContactSeconds)} human_first_fire={FormatTime(_humanFirstFireSeconds)} human_first_hit={FormatTime(_humanFirstHitSeconds)} " +
            $"human_spawned={humanSpawned} human_agent_coverage={_policyControlledShipIds[1].Count}/{_policyEligibleShipIds[1].Count} " +
            $"human_weapons={FormatWeaponActivity(1)} " +
            $"human_rewards=terminal:{humanTerminal:F4},tsv:{_humanTsvRewardThisEpisode:F4},time:{humanTimeReward:F4},total:{LastEpisodeResult.HumanTotalReward:F4}");

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

        if (result.TimedOut)
        {
            _beeLosses++;
            _humanLosses++;
            _timeouts++;
        }
        else if (result.WinningSide == 0)
        {
            _draws++;
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
            $"RL 1v1 summary episodes={_completedEpisodes} bee_record={_beeWins}-{_beeLosses} human_record={_humanWins}-{_humanLosses} " +
            $"draws={_draws} timeouts={_timeouts} avg_duration={averageDuration:F2}s " +
            $"bee_shots={_beeShotsTotal} bee_hits={_beeHitsTotal} bee_hit_rate={beeHitRate:P2} bee_damage={_beeDamageTotal} " +
            $"human_shots={_humanShotsTotal} human_hits={_humanHitsTotal} human_hit_rate={humanHitRate:P2} human_damage={_humanDamageTotal}");
    }

    private float ElapsedEpisodeSeconds => Mathf.Max(0f, Time.time - _episodeStartedAt);

    private int CountSpawnedShips(int sideIndex)
    {
        int count = 0;
        foreach (long shipId in _seenShipIds[sideIndex])
        {
            if (!_initialShipIds[sideIndex].Contains(shipId))
            {
                count++;
            }
        }
        return count;
    }

    private string FormatWeaponActivity(int sideIndex)
    {
        HashSet<ConfigData.WeaponTypes> weaponTypes = new HashSet<ConfigData.WeaponTypes>();
        foreach (ConfigData.WeaponTypes type in _fireRequestsByWeapon[sideIndex].Keys)
        {
            weaponTypes.Add(type);
        }
        foreach (ConfigData.WeaponTypes type in _shotsByWeapon[sideIndex].Keys)
        {
            weaponTypes.Add(type);
        }
        if (weaponTypes.Count == 0)
        {
            return "none";
        }

        List<string> values = new List<string>();
        foreach (ConfigData.WeaponTypes type in weaponTypes)
        {
            _fireRequestsByWeapon[sideIndex].TryGetValue(type, out int requests);
            _shotsByWeapon[sideIndex].TryGetValue(type, out int shots);
            values.Add($"{type}:{requests}/{shots}");
        }
        return string.Join("|", values);
    }

    private static string FormatTime(float seconds)
    {
        return seconds < 0f ? "none" : $"{seconds:F2}s";
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
