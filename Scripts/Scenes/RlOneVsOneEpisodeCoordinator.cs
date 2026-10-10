using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Owns episode reward bookkeeping and lightweight training diagnostics for one dedicated RL combat
/// arena. Static combat hooks route to the coordinator registered for the affected Level so multiple
/// Levels can train independently inside one Unity process.
/// </summary>
[DefaultExecutionOrder(-5000)]
internal sealed class RlOneVsOneEpisodeCoordinator : MonoBehaviour
{
    private const int EpisodeMetricsLogInterval = 1;
    private const int SummaryIntervalEpisodes = 100;
    private const int FullEpisodeDiagnosticsInterval = 1000;
    private const long TrainingDiagnosticMaxBytes = 8L * 1024L * 1024L;
    private const long CommunicationDiagnosticMaxBytes = 48L * 1024L * 1024L;
    private const int CommunicationDiagnosticFlushChars = 4 * 1024 * 1024;
    private static readonly object TrainingDiagnosticLogLock = new object();
    private static readonly object CommunicationDiagnosticLogLock = new object();
    private static readonly Encoding TrainingDiagnosticEncoding = new UTF8Encoding(false);
    private static bool _trainingDiagnosticWriteWarningEmitted;
    private static bool _communicationDiagnosticWriteWarningEmitted;
    private static int _communicationDiagnosticSegment;

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
        internal readonly float BeeEconomicReward;
        internal readonly float BeeRetainedMiningReward;
        internal readonly float BeeTotalReward;
        internal readonly float HumanTerminalReward;
        internal readonly float HumanTsvReward;
        internal readonly float HumanTimeReward;
        internal readonly float HumanEconomicReward;
        internal readonly float HumanRetainedMiningReward;
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
            float beeEconomicReward,
            float beeRetainedMiningReward,
            float humanTerminalReward,
            float humanTsvReward,
            float humanTimeReward,
            float humanEconomicReward,
            float humanRetainedMiningReward)
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
            BeeEconomicReward = beeEconomicReward;
            BeeRetainedMiningReward = beeRetainedMiningReward;
            BeeTotalReward = beeTerminalReward + beeTsvReward + beeTimeReward + beeEconomicReward;
            HumanTerminalReward = humanTerminalReward;
            HumanTsvReward = humanTsvReward;
            HumanTimeReward = humanTimeReward;
            HumanEconomicReward = humanEconomicReward;
            HumanRetainedMiningReward = humanRetainedMiningReward;
            HumanTotalReward = humanTerminalReward + humanTsvReward + humanTimeReward + humanEconomicReward;
        }
    }

    internal static event Action<Level, int, float> TsvRewardOccurred;
    internal static event Action<Level, int, float> EconomicRewardOccurred;
    internal static event Action<Level, EpisodeResult> EpisodeEnded;
    internal static EpisodeResult LastEpisodeResult { get; private set; }

    private static readonly Dictionary<Level, RlOneVsOneEpisodeCoordinator> Coordinators =
        new Dictionary<Level, RlOneVsOneEpisodeCoordinator>();

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
    private int _beeTurretStaticObstacleImpactsThisEpisode;
    private int _humanTurretStaticObstacleImpactsThisEpisode;
    private int _beeFireRequestsThisEpisode;
    private int _humanFireRequestsThisEpisode;
    private int _beeHitsThisEpisode;
    private int _humanHitsThisEpisode;
    private int _beeTurretHitsThisEpisode;
    private int _humanTurretHitsThisEpisode;
    private int _beeSpecialHitsThisEpisode;
    private int _humanSpecialHitsThisEpisode;
    private int _beeOtherHitsThisEpisode;
    private int _humanOtherHitsThisEpisode;
    private int _beeDamageThisEpisode;
    private int _humanDamageThisEpisode;
    private int _beeTurretDamageThisEpisode;
    private int _humanTurretDamageThisEpisode;
    private int _beeSpecialDamageThisEpisode;
    private int _humanSpecialDamageThisEpisode;
    private int _beeOtherDamageThisEpisode;
    private int _humanOtherDamageThisEpisode;
    private float _beeTsvRewardThisEpisode;
    private float _humanTsvRewardThisEpisode;
    private float _beeEconomicRewardThisEpisode;
    private float _humanEconomicRewardThisEpisode;
    private int _beeDestroyedMinedTsvThisEpisode;
    private int _humanDestroyedMinedTsvThisEpisode;
    private int _beeRetainedMinedTsvThisEpisode;
    private int _humanRetainedMinedTsvThisEpisode;
    private float _beeFirstContactSeconds;
    private float _humanFirstContactSeconds;
    private float _beeFirstFireSeconds;
    private float _humanFirstFireSeconds;
    private float _beeFirstHitSeconds;
    private float _humanFirstHitSeconds;
    private bool _beeHasVisibleEnemy;
    private bool _humanHasVisibleEnemy;
    private bool _beeEverHadVisibleEnemy;
    private bool _humanEverHadVisibleEnemy;
    private float _beeVisibilityStateStartedAt;
    private float _humanVisibilityStateStartedAt;
    private float _beeNoEnemyVisibleSeconds;
    private float _humanNoEnemyVisibleSeconds;
    private float _beeLostContactSeconds;
    private float _humanLostContactSeconds;
    private int _beeContactLossCount;
    private int _humanContactLossCount;

    private readonly StringBuilder _communicationTrace = new StringBuilder();
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
    private readonly Dictionary<string, float>[] _rewardSources =
    {
        new Dictionary<string, float>(StringComparer.Ordinal),
        new Dictionary<string, float>(StringComparer.Ordinal)
    };
    private readonly bool[] _hasRecordedDeathAttribution = new bool[2];
    private readonly bool[] _lastDeathWasOpponentCaused = new bool[2];
    private readonly HashSet<long>[] _rewardedShipDiscoveryIds = { new HashSet<long>(), new HashSet<long>() };
    private readonly HashSet<int>[] _rewardedMiningAsteroidDiscoveryIds = { new HashSet<int>(), new HashSet<int>() };
    private readonly HashSet<int>[] _rewardedObstacleDiscoveryIds = { new HashSet<int>(), new HashSet<int>() };
    private readonly HashSet<int>[] _rewardedMapObjectDiscoveryIds = { new HashSet<int>(), new HashSet<int>() };

    // Mined resources remain attached to the individual mining ship until that ship is safely
    // retained. Destroyed miners forfeit their cargo; end-killed/warped ships remain retained.
    private readonly Dictionary<long, int> _minedTsvByShipId = new Dictionary<long, int>();
    private readonly Dictionary<long, int> _miningShipSideById = new Dictionary<long, int>();
    private readonly HashSet<long> _forfeitedMiningShipIds = new HashSet<long>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ResetCoordinatorRegistry()
    {
        Coordinators.Clear();
    }

    private static RlOneVsOneEpisodeCoordinator GetCoordinator(Level level, bool createIfMissing = true)
    {
        if (level == null || level.Stage == null || !RlOneVsOneTrainingBootstrap.IsActiveFor(level.Stage))
        {
            return null;
        }

        if (Coordinators.TryGetValue(level, out RlOneVsOneEpisodeCoordinator coordinator) && coordinator != null)
        {
            return coordinator;
        }
        Coordinators.Remove(level);

        coordinator = level.GetComponent<RlOneVsOneEpisodeCoordinator>();
        if (coordinator == null && createIfMissing)
        {
            coordinator = level.gameObject.AddComponent<RlOneVsOneEpisodeCoordinator>();
        }
        if (coordinator != null)
        {
            coordinator._stage = level.Stage;
            coordinator._level = level;
            Coordinators[level] = coordinator;
        }
        return coordinator;
    }

    private static RlOneVsOneEpisodeCoordinator GetCoordinator(Ship ship)
    {
        return ship != null ? GetCoordinator(ship.Level) : null;
    }

    private void OnDestroy()
    {
        FlushCommunicationDiagnostics();
        RlOneVsOneEpisodeDiagnostics.End(_level);
        if (_level != null && Coordinators.TryGetValue(_level, out RlOneVsOneEpisodeCoordinator coordinator) && coordinator == this)
        {
            Coordinators.Remove(_level);
        }
    }

    private readonly RlTeamExplorationGrid _beeExplorationGrid = new RlTeamExplorationGrid();
    private readonly RlTeamExplorationGrid _humanExplorationGrid = new RlTeamExplorationGrid();

    private void Update()
    {
        if (_stage == null || !_stage.IsTrainingNueralNetwork || _level == null || _level.State == null)
        {
            return;
        }

        if (!_episodeActive)
        {
            TryBeginEpisode(_level);
        }
        if (_episodeActive)
        {
            TrackEpisodeShips(_level);
        }
        if (_episodeActive && !_discoveryRewardsReady)
        {
            TryEnableDiscoveryRewards(_level);
        }
    }

    internal static float GetExplorationFreshness(Level level, int side, int worldIndex)
    {
        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(level, false);
        if (coordinator == null || !coordinator._episodeActive || ConfigData.Configuration == null)
        {
            return 0f;
        }

        float progress = level.GetNormalizedRlEpisodeProgress();
        if (side == ConfigData.Configuration.BeeSide)
        {
            return coordinator._beeExplorationGrid.GetFreshness(worldIndex, progress);
        }
        if (side == ConfigData.Configuration.HumanSide)
        {
            return coordinator._humanExplorationGrid.GetFreshness(worldIndex, progress);
        }
        return 0f;
    }

    internal static bool IsControllerForSide(Level level, int side, int teamId)
    {
        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(level);
        if (coordinator == null || ConfigData.Configuration == null)
        {
            return false;
        }
        coordinator.TryBeginEpisode(level);
        if (!coordinator._episodeActive)
        {
            return false;
        }

        if (side == ConfigData.Configuration.BeeSide)
        {
            return teamId == coordinator._beeTeamId;
        }
        if (side == ConfigData.Configuration.HumanSide)
        {
            return teamId == coordinator._humanTeamId;
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
        if (!TryGetTrackedSide(ship, out RlOneVsOneEpisodeCoordinator coordinator, out int sideIndex))
        {
            return;
        }

        if (sideIndex == 0)
        {
            coordinator._beeFireRequestsThisEpisode++;
        }
        else
        {
            coordinator._humanFireRequestsThisEpisode++;
        }
        IncrementWeaponCount(coordinator._fireRequestsByWeapon[sideIndex], weapon);
    }

    internal static void RecordShotFired(Ship ship, Weapon weapon)
    {
        if (!TryGetTrackedSide(ship, out RlOneVsOneEpisodeCoordinator coordinator, out int sideIndex))
        {
            return;
        }

        if (sideIndex == 0)
        {
            coordinator._beeShotsThisEpisode++;
            if (coordinator._beeFirstFireSeconds < 0f)
            {
                coordinator._beeFirstFireSeconds = coordinator.ElapsedEpisodeSeconds;
            }
        }
        else
        {
            coordinator._humanShotsThisEpisode++;
            if (coordinator._humanFirstFireSeconds < 0f)
            {
                coordinator._humanFirstFireSeconds = coordinator.ElapsedEpisodeSeconds;
            }
        }
        IncrementWeaponCount(coordinator._shotsByWeapon[sideIndex], weapon);
    }

    internal static void RecordProjectileStaticObstacleImpact(
        Projectile projectile,
        Obstacle obstacle)
    {
        if (projectile == null ||
            obstacle == null ||
            obstacle.ObstacleType != ConfigData.ObstacleTypes.StaticObstacle ||
            !(projectile.Weapon is Turret) ||
            !TryGetTrackedSide(
                projectile.Shooter,
                out RlOneVsOneEpisodeCoordinator coordinator,
                out int sideIndex))
        {
            return;
        }

        if (sideIndex == 0)
        {
            coordinator._beeTurretStaticObstacleImpactsThisEpisode++;
        }
        else
        {
            coordinator._humanTurretStaticObstacleImpactsThisEpisode++;
        }
    }

    private static bool TryGetTrackedSide(
        Ship ship,
        out RlOneVsOneEpisodeCoordinator coordinator,
        out int sideIndex)
    {
        coordinator = GetCoordinator(ship);
        sideIndex = -1;
        if (coordinator == null || ConfigData.Configuration == null || ship == null)
        {
            return false;
        }
        coordinator.TryBeginEpisode(ship.Level);
        if (!coordinator._episodeActive || ship.Level != coordinator._level)
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

    private static bool HasPersistentFleetValue(Ship ship)
    {
        return ship != null &&
               !ship.IsMinionShip &&
               !ship.IsCarrierShip &&
               (ship.Squad == null || !ship.Squad.IsMinionSquad);
    }

    /// <summary>
    /// Records attributed ship damage after normal damage/TSV calculation has succeeded. Enemy
    /// damage credits the attacker and penalizes the target. Friendly fire penalizes the damaged
    /// side only, so same-side credit can never cancel the casualty-preservation signal.
    /// Free temporary/minion ships remain tactical assets but do not contribute persistent-fleet
    /// TSV shaping when they take damage.
    /// </summary>
    internal static void RecordHit(Ship attacker, Ship target, int damage, int tsvLoss)
    {
        RecordHitWithSource(attacker, target, damage, tsvLoss, "gun");
    }

    internal static void RecordHitWithSource(
        Ship attacker,
        Ship target,
        int damage,
        int tsvLoss,
        string damageSource)
    {
        if (ConfigData.Configuration == null || attacker == null || target == null ||
            attacker.Level == null || attacker.Level != target.Level)
        {
            return;
        }

        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(attacker.Level);
        if (coordinator == null)
        {
            return;
        }
        coordinator.TryBeginEpisode(attacker.Level);
        if (!coordinator._episodeActive)
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
        int minedCargoForfeit = target.Health <= 0 ? GetShipMinedTsv(target) : 0;
        int appliedTsvLoss = HasPersistentFleetValue(target)
            ? Mathf.Max(0, tsvLoss - minedCargoForfeit)
            : 0;

        if (isEnemyDamage && appliedDamage > 0)
        {
            bool specialActionHit = IsSpecialActionDamageSource(damageSource);
            bool turretHit = IsTurretDamageSource(damageSource);
            if (attacker.Side == beeSide)
            {
                coordinator._beeHitsThisEpisode++;
                coordinator._beeDamageThisEpisode += appliedDamage;
                if (specialActionHit)
                {
                    coordinator._beeSpecialHitsThisEpisode++;
                    coordinator._beeSpecialDamageThisEpisode += appliedDamage;
                }
                else if (turretHit)
                {
                    coordinator._beeTurretHitsThisEpisode++;
                    coordinator._beeTurretDamageThisEpisode += appliedDamage;
                }
                else
                {
                    coordinator._beeOtherHitsThisEpisode++;
                    coordinator._beeOtherDamageThisEpisode += appliedDamage;
                }
                if (coordinator._beeFirstHitSeconds < 0f)
                {
                    coordinator._beeFirstHitSeconds = coordinator.ElapsedEpisodeSeconds;
                }
            }
            else
            {
                coordinator._humanHitsThisEpisode++;
                coordinator._humanDamageThisEpisode += appliedDamage;
                if (specialActionHit)
                {
                    coordinator._humanSpecialHitsThisEpisode++;
                    coordinator._humanSpecialDamageThisEpisode += appliedDamage;
                }
                else if (turretHit)
                {
                    coordinator._humanTurretHitsThisEpisode++;
                    coordinator._humanTurretDamageThisEpisode += appliedDamage;
                }
                else
                {
                    coordinator._humanOtherHitsThisEpisode++;
                    coordinator._humanOtherDamageThisEpisode += appliedDamage;
                }
                if (coordinator._humanFirstHitSeconds < 0f)
                {
                    coordinator._humanFirstHitSeconds = coordinator.ElapsedEpisodeSeconds;
                }
            }
        }

        if (appliedTsvLoss <= 0)
        {
            return;
        }

        int combinedStartingTsv = Mathf.Max(1, coordinator._beeStartingTsv + coordinator._humanStartingTsv);
        float reward = RlOneVsOneReward.CalculateTsvLossReward(appliedTsvLoss, combinedStartingTsv);
        if (isEnemyDamage)
        {
            coordinator.ApplyImmediateTsvReward(attacker.Side, reward, "damage_dealt");
        }
        coordinator.ApplyImmediateTsvReward(target.Side, -reward, "damage_taken");
    }

    private static bool IsTurretDamageSource(string damageSource)
    {
        return string.IsNullOrEmpty(damageSource) ||
               string.Equals(damageSource, "gun", StringComparison.Ordinal);
    }

    internal static bool IsSpecialActionDamageSource(string damageSource)
    {
        return string.Equals(damageSource, "bomb", StringComparison.Ordinal) ||
               string.Equals(damageSource, "charge", StringComparison.Ordinal) ||
               string.Equals(damageSource, "explosion", StringComparison.Ordinal);
    }

    internal static void RecordCommunicationAction(Ship ship, Vector4 communication)
    {
        if (!RlOneVsOneTrainingBootstrap.CurrentCommunicationLoggingEnabled ||
            !TryGetTrackedSide(ship, out RlOneVsOneEpisodeCoordinator coordinator, out int sideIndex))
        {
            return;
        }

        string sideName = sideIndex == 0 ? "bee" : "human";
        int teamId = sideIndex == 0 ? coordinator._beeTeamId : coordinator._humanTeamId;
        Vector2 position = ship.GetPosition();
        float healthFraction = ship.MaxHealth > 0
            ? Mathf.Clamp01((float)ship.Health / ship.MaxHealth)
            : 0f;
        coordinator._communicationTrace.Append(
            $"RL comm episode={coordinator._episodeNumber} arena={coordinator.GetArenaIndex()} t={coordinator.ElapsedEpisodeSeconds:F3}s " +
            $"side={sideName} team={teamId} ship_id={ship.Id} ship_type={ship.ShipType} " +
            $"x={position.x:F2} y={position.y:F2} health={healthFraction:F4} " +
            $"c0={communication.x:F5} c1={communication.y:F5} c2={communication.z:F5} c3={communication.w:F5}");
        coordinator._communicationTrace.AppendLine();
        if (coordinator._communicationTrace.Length >= CommunicationDiagnosticFlushChars)
        {
            coordinator.FlushCommunicationDiagnostics();
        }
    }

    internal static void RecordShipDeathAttribution(
        Ship victim,
        Ship killer,
        bool endKill,
        bool explicitlyNonOpponentCaused)
    {
        if (victim == null || ConfigData.Configuration == null)
        {
            return;
        }

        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(victim);
        if (coordinator == null || !coordinator._episodeActive)
        {
            return;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        int sideIndex = victim.Side == beeSide ? 0 :
            victim.Side == humanSide ? 1 : -1;
        if (sideIndex < 0)
        {
            return;
        }

        // A same-side explosive can be a chain reaction owned by an opposing killer. Preserve that
        // ownership so an enemy-triggered Fire Barge explosion remains an ordinary opponent-caused
        // loss. Explicit self-detonations override historical gameplay killer attribution.
        Ship effectiveKiller = killer;
        int attributionDepth = 0;
        while (!explicitlyNonOpponentCaused &&
               effectiveKiller != null &&
               effectiveKiller.Side == victim.Side &&
               effectiveKiller.Killer != null &&
               effectiveKiller.Killer != effectiveKiller &&
               attributionDepth++ < 16)
        {
            effectiveKiller = effectiveKiller.Killer;
        }

        bool opponentCaused =
            !explicitlyNonOpponentCaused &&
            effectiveKiller != null &&
            effectiveKiller.Side != victim.Side &&
            (effectiveKiller.Side == beeSide || effectiveKiller.Side == humanSide);

        coordinator._hasRecordedDeathAttribution[sideIndex] = true;
        coordinator._lastDeathWasOpponentCaused[sideIndex] = opponentCaused;
    }

    internal static void RecordUnattributedTsvLoss(Ship target, int tsvLoss)
    {
        if (ConfigData.Configuration == null || target == null)
        {
            return;
        }

        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(target);
        if (coordinator == null)
        {
            return;
        }
        coordinator.TryBeginEpisode(target.Level);
        if (!coordinator._episodeActive)
        {
            return;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        if (target.Side != beeSide && target.Side != humanSide)
        {
            return;
        }

        int minedCargoForfeit = target.Health <= 0 ? GetShipMinedTsv(target) : 0;
        int appliedTsvLoss = HasPersistentFleetValue(target)
            ? Mathf.Max(0, tsvLoss - minedCargoForfeit)
            : 0;
        if (appliedTsvLoss <= 0)
        {
            return;
        }

        int combinedStartingTsv = Mathf.Max(1, coordinator._beeStartingTsv + coordinator._humanStartingTsv);
        float reward = RlOneVsOneReward.CalculateTsvLossReward(appliedTsvLoss, combinedStartingTsv);
        coordinator.ApplyImmediateTsvReward(target.Side, -reward, "hazard_loss");
    }

    private static int GetShipMinedTsv(Ship ship)
    {
        return ship?.FleetShip != null ? Mathf.Max(0, ship.FleetShip.MineralsMinedThisLevel) : 0;
    }

    internal static int GetStartingTsv(Level level, int side)
    {
        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(level, false);
        if (coordinator != null && coordinator._episodeActive && ConfigData.Configuration != null)
        {
            if (side == ConfigData.Configuration.BeeSide)
            {
                return Mathf.Max(1, coordinator._beeStartingTsv);
            }
            if (side == ConfigData.Configuration.HumanSide)
            {
                return Mathf.Max(1, coordinator._humanStartingTsv);
            }
        }

        if (level?.State?.InitialTsv != null && side > 0 && side <= level.State.InitialTsv.Length)
        {
            return Mathf.Max(1, level.State.InitialTsv[side - 1]);
        }
        return 1;
    }

    internal static int GetRetainableMinedTsv(Level level, int side)
    {
        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(level, false);
        if (coordinator != null && coordinator._episodeActive)
        {
            int tracked = 0;
            foreach (KeyValuePair<long, int> entry in coordinator._minedTsvByShipId)
            {
                if (coordinator._forfeitedMiningShipIds.Contains(entry.Key) ||
                    !coordinator._miningShipSideById.TryGetValue(entry.Key, out int trackedSide) ||
                    trackedSide != side)
                {
                    continue;
                }
                tracked += Mathf.Max(0, entry.Value);
            }
            return tracked;
        }

        int total = 0;
        List<Ship> ships = level?.State?.GetShips(side);
        if (ships == null)
        {
            return 0;
        }
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship != null && !ship.IsDead)
            {
                total += GetShipMinedTsv(ship);
            }
        }
        return total;
    }

    internal static void RecordMiningValueUpdated(Ship ship)
    {
        if (!TryGetTrackedSide(ship, out RlOneVsOneEpisodeCoordinator coordinator, out _))
        {
            return;
        }

        int minedTsv = GetShipMinedTsv(ship);
        if (minedTsv <= 0)
        {
            return;
        }

        coordinator._minedTsvByShipId[ship.Id] = minedTsv;
        coordinator._miningShipSideById[ship.Id] = ship.Side;
        coordinator._forfeitedMiningShipIds.Remove(ship.Id);
    }

    internal static void RecordMiningShipExit(Ship ship, Ship killer, bool safelyRetained)
    {
        if (ship == null || ConfigData.Configuration == null)
        {
            return;
        }

        // Ship cleanup can happen after CompleteEpisode. Do not let a reset-time EndKill
        // accidentally start a fresh episode merely to settle cargo from the completed one.
        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(ship.Level, false);
        if (coordinator == null || !coordinator._episodeActive || ship.Level != coordinator._level ||
            (ship.Side != ConfigData.Configuration.BeeSide &&
             ship.Side != ConfigData.Configuration.HumanSide))
        {
            return;
        }

        int minedTsv = Mathf.Max(
            GetShipMinedTsv(ship),
            coordinator._minedTsvByShipId.TryGetValue(ship.Id, out int tracked) ? tracked : 0);
        if (minedTsv <= 0)
        {
            return;
        }

        coordinator._minedTsvByShipId[ship.Id] = minedTsv;
        coordinator._miningShipSideById[ship.Id] = ship.Side;
        if (safelyRetained)
        {
            return;
        }

        // This cargo can no longer earn a terminal retained-mining reward.
        if (!coordinator._forfeitedMiningShipIds.Add(ship.Id))
        {
            return;
        }

        if (killer == null || killer.Side == ship.Side ||
            ConfigData.Configuration == null ||
            (killer.Side != ConfigData.Configuration.BeeSide &&
             killer.Side != ConfigData.Configuration.HumanSide))
        {
            return;
        }

        float reward = RlOneVsOneReward.CalculateEconomicValueReward(
            minedTsv,
            GetStartingTsv(ship.Level, killer.Side));
        coordinator.ApplyEconomicReward(killer.Side, reward, "enemy_mined_cargo");
        if (killer.Side == ConfigData.Configuration.BeeSide)
        {
            coordinator._beeDestroyedMinedTsvThisEpisode += minedTsv;
        }
        else
        {
            coordinator._humanDestroyedMinedTsvThisEpisode += minedTsv;
        }
    }

    internal static void RecordSuccessfulCapabilityOutcome(Ship ship, int tsvValue)
    {
        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(ship);
        if (coordinator == null || ship == null)
        {
            return;
        }
        coordinator.TryBeginEpisode(ship.Level);
        if (!coordinator._episodeActive)
        {
            return;
        }

        int value = Mathf.Max(0, tsvValue);
        if (value <= 0)
        {
            return;
        }

        int combinedStartingTsv = Mathf.Max(1, coordinator._beeStartingTsv + coordinator._humanStartingTsv);
        float reward = RlOneVsOneReward.CalculateTsvLossReward(value, combinedStartingTsv);
        coordinator.ApplyImmediateTsvReward(ship.Side, reward, "capability");
    }

    internal static void RecordShipDiscovery(Ship observer, Ship spotted)
    {
        if (!TryPrepareDiscovery(observer, out RlOneVsOneEpisodeCoordinator coordinator, out int sideIndex) ||
            spotted == null || spotted.IsDead || spotted.Level != observer.Level || spotted.Side == observer.Side)
        {
            return;
        }

        coordinator.AwardShipDiscovery(observer.Side, sideIndex, spotted);
    }

    internal static void RecordMiningAsteroidDiscovery(Ship observer, MiningAsteroid asteroid)
    {
        if (!TryPrepareDiscovery(observer, out RlOneVsOneEpisodeCoordinator coordinator, out int sideIndex) ||
            asteroid == null || asteroid.IsDead || asteroid.Level != observer.Level)
        {
            return;
        }
        coordinator.AwardMiningAsteroidDiscovery(observer.Side, sideIndex, asteroid);
    }

    internal static void RecordMapObjectDiscovery(Ship observer, MapObject mapObject)
    {
        if (!TryPrepareDiscovery(observer, out RlOneVsOneEpisodeCoordinator coordinator, out int sideIndex) ||
            mapObject == null || mapObject.IsDead || mapObject.Level != observer.Level)
        {
            return;
        }
        coordinator.AwardMapObjectDiscovery(observer.Side, sideIndex, mapObject);
    }

    internal static void RecordObstacleDiscovery(Ship observer, Obstacle obstacle)
    {
        if (!TryPrepareDiscovery(observer, out RlOneVsOneEpisodeCoordinator coordinator, out int sideIndex) ||
            obstacle == null || obstacle.IsDead || obstacle.Level != observer.Level)
        {
            return;
        }
        coordinator.AwardObstacleDiscovery(observer.Side, sideIndex, obstacle);
    }

    private static bool TryPrepareDiscovery(
        Ship observer,
        out RlOneVsOneEpisodeCoordinator coordinator,
        out int sideIndex)
    {
        coordinator = null;
        sideIndex = -1;
        if (ConfigData.Configuration == null || observer == null || observer.IsDead || observer.Level == null)
        {
            return false;
        }

        coordinator = GetCoordinator(observer.Level);
        if (coordinator == null)
        {
            return false;
        }
        coordinator.TryBeginEpisode(observer.Level);
        if (!coordinator._episodeActive || !coordinator._discoveryRewardsReady)
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

        // First sighting of every enemy ship, including free tactical children/minions, uses the
        // same TSV-scaled enemy-discovery shaping. Children remain excluded only from persistent
        // fleet-value damage/loss shaping.
        float reward = RlOneVsOneReward.CalculateStaticDiscoveryReward(
            Mathf.Max(1, spotted.Tsv),
            _enemyShipDiscoveryValue[sideIndex],
            RlOneVsOneReward.EnemyShipDiscoveryBudget);
        ApplyImmediateTsvReward(side, reward, "discovery");
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
        ApplyImmediateTsvReward(side, reward, "discovery");
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
        ApplyImmediateTsvReward(side, reward, "discovery");
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
        ApplyImmediateTsvReward(side, reward, "discovery");
    }

    internal static void CompleteElimination(Level level)
    {
        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(level);
        if (coordinator == null)
        {
            return;
        }
        coordinator.TryBeginEpisode(level);
        if (coordinator._episodeActive)
        {
            coordinator.CompleteEpisode(level, DetermineWinner(level), false);
        }
    }

    internal static void CompleteTimeout(Level level)
    {
        RlOneVsOneEpisodeCoordinator coordinator = GetCoordinator(level);
        if (coordinator == null)
        {
            return;
        }
        coordinator.TryBeginEpisode(level);
        if (coordinator._episodeActive)
        {
            coordinator.CompleteEpisode(level, 0, true);
        }
    }

    private void TryBeginEpisode(Level level)
    {
        if (level == null || level != _level || level.State == null || ConfigData.Configuration == null)
        {
            return;
        }
        if (_episodeActive)
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
            level.State.GameOver || level.IsRestarting)
        {
            return;
        }

        _episodeNumber++;
        _beeTeamId = GetTeamIdForSide(beeSide, _episodeNumber);
        _humanTeamId = GetTeamIdForSide(humanSide, _episodeNumber);
        _episodeStartedAt = Time.time;
        _beeStartingTsv = beeStartingTsv;
        _humanStartingTsv = humanStartingTsv;
        _beeShotsThisEpisode = 0;
        _humanShotsThisEpisode = 0;
        _beeTurretStaticObstacleImpactsThisEpisode = 0;
        _humanTurretStaticObstacleImpactsThisEpisode = 0;
        _beeFireRequestsThisEpisode = 0;
        _humanFireRequestsThisEpisode = 0;
        _beeHitsThisEpisode = 0;
        _humanHitsThisEpisode = 0;
        _beeTurretHitsThisEpisode = 0;
        _humanTurretHitsThisEpisode = 0;
        _beeSpecialHitsThisEpisode = 0;
        _humanSpecialHitsThisEpisode = 0;
        _beeOtherHitsThisEpisode = 0;
        _humanOtherHitsThisEpisode = 0;
        _beeDamageThisEpisode = 0;
        _humanDamageThisEpisode = 0;
        _beeTurretDamageThisEpisode = 0;
        _humanTurretDamageThisEpisode = 0;
        _beeSpecialDamageThisEpisode = 0;
        _humanSpecialDamageThisEpisode = 0;
        _beeOtherDamageThisEpisode = 0;
        _humanOtherDamageThisEpisode = 0;
        _beeTsvRewardThisEpisode = 0f;
        _humanTsvRewardThisEpisode = 0f;
        _rewardSources[0].Clear();
        _rewardSources[1].Clear();
        _beeEconomicRewardThisEpisode = 0f;
        _humanEconomicRewardThisEpisode = 0f;
        _hasRecordedDeathAttribution[0] = false;
        _hasRecordedDeathAttribution[1] = false;
        _lastDeathWasOpponentCaused[0] = false;
        _lastDeathWasOpponentCaused[1] = false;
        _beeDestroyedMinedTsvThisEpisode = 0;
        _humanDestroyedMinedTsvThisEpisode = 0;
        _beeRetainedMinedTsvThisEpisode = 0;
        _humanRetainedMinedTsvThisEpisode = 0;
        _minedTsvByShipId.Clear();
        _miningShipSideById.Clear();
        _forfeitedMiningShipIds.Clear();
        _beeFirstContactSeconds = -1f;
        _humanFirstContactSeconds = -1f;
        _beeFirstFireSeconds = -1f;
        _humanFirstFireSeconds = -1f;
        _beeFirstHitSeconds = -1f;
        _humanFirstHitSeconds = -1f;
        _beeHasVisibleEnemy = false;
        _humanHasVisibleEnemy = false;
        _beeEverHadVisibleEnemy = false;
        _humanEverHadVisibleEnemy = false;
        _beeVisibilityStateStartedAt = 0f;
        _humanVisibilityStateStartedAt = 0f;
        _beeNoEnemyVisibleSeconds = 0f;
        _humanNoEnemyVisibleSeconds = 0f;
        _beeLostContactSeconds = 0f;
        _humanLostContactSeconds = 0f;
        _beeContactLossCount = 0;
        _humanContactLossCount = 0;
        _beeExplorationGrid.Reset();
        _humanExplorationGrid.Reset();
        _communicationTrace.Clear();
        ResetShipDiagnostics(beeShips, humanShips);
        RlOneVsOneEpisodeDiagnostics.Begin(level);
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
        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        List<Ship> beeShips = level.State.GetShips(beeSide);
        List<Ship> humanShips = level.State.GetShips(humanSide);
        TrackSideShips(beeShips, 0);
        TrackSideShips(humanShips, 1);
        float episodeProgress = level.GetNormalizedRlEpisodeProgress();
        _beeExplorationGrid.Update(level, beeShips, episodeProgress);
        _humanExplorationGrid.Update(level, humanShips, episodeProgress);
        TrackEnemyVisibility(level, beeSide, humanSide);
        RlOneVsOneEpisodeDiagnostics.Track(level);
    }

    private void TrackEnemyVisibility(Level level, int beeSide, int humanSide)
    {
        TrackSideEnemyVisibility(level, beeSide, 0);
        TrackSideEnemyVisibility(level, humanSide, 1);
    }

    private void TrackSideEnemyVisibility(Level level, int observerSide, int sideIndex)
    {
        bool hasVisibleEnemy = level.State.HasLiveEnemyVisibleToHiveMind(observerSide);

        float elapsed = ElapsedEpisodeSeconds;
        bool previousVisible = sideIndex == 0 ? _beeHasVisibleEnemy : _humanHasVisibleEnemy;
        if (hasVisibleEnemy == previousVisible)
        {
            return;
        }

        float stateStartedAt = sideIndex == 0 ? _beeVisibilityStateStartedAt : _humanVisibilityStateStartedAt;
        float stateDuration = Mathf.Max(0f, elapsed - stateStartedAt);
        bool everHadVisibleEnemy = sideIndex == 0 ? _beeEverHadVisibleEnemy : _humanEverHadVisibleEnemy;

        if (!previousVisible)
        {
            if (sideIndex == 0)
            {
                _beeNoEnemyVisibleSeconds += stateDuration;
                if (!everHadVisibleEnemy)
                {
                    _beeEverHadVisibleEnemy = true;
                    if (_beeFirstContactSeconds < 0f)
                    {
                        _beeFirstContactSeconds = elapsed;
                    }
                }
                else
                {
                    _beeLostContactSeconds += stateDuration;
                }
            }
            else
            {
                _humanNoEnemyVisibleSeconds += stateDuration;
                if (!everHadVisibleEnemy)
                {
                    _humanEverHadVisibleEnemy = true;
                    if (_humanFirstContactSeconds < 0f)
                    {
                        _humanFirstContactSeconds = elapsed;
                    }
                }
                else
                {
                    _humanLostContactSeconds += stateDuration;
                }
            }
        }
        else if (everHadVisibleEnemy)
        {
            if (sideIndex == 0)
            {
                _beeContactLossCount++;
            }
            else
            {
                _humanContactLossCount++;
            }
        }

        if (sideIndex == 0)
        {
            _beeHasVisibleEnemy = hasVisibleEnemy;
            _beeVisibilityStateStartedAt = elapsed;
        }
        else
        {
            _humanHasVisibleEnemy = hasVisibleEnemy;
            _humanVisibilityStateStartedAt = elapsed;
        }
    }

    private void FinalizeEnemyVisibilityDiagnostics(float durationSeconds)
    {
        FinalizeSideEnemyVisibility(0, durationSeconds);
        FinalizeSideEnemyVisibility(1, durationSeconds);
    }

    private void FinalizeSideEnemyVisibility(int sideIndex, float durationSeconds)
    {
        bool visible = sideIndex == 0 ? _beeHasVisibleEnemy : _humanHasVisibleEnemy;
        float stateStartedAt = sideIndex == 0 ? _beeVisibilityStateStartedAt : _humanVisibilityStateStartedAt;
        float stateDuration = Mathf.Max(0f, durationSeconds - stateStartedAt);
        bool everHadVisibleEnemy = sideIndex == 0 ? _beeEverHadVisibleEnemy : _humanEverHadVisibleEnemy;

        if (!visible)
        {
            if (sideIndex == 0)
            {
                _beeNoEnemyVisibleSeconds += stateDuration;
                if (everHadVisibleEnemy)
                {
                    _beeLostContactSeconds += stateDuration;
                }
            }
            else
            {
                _humanNoEnemyVisibleSeconds += stateDuration;
                if (everHadVisibleEnemy)
                {
                    _humanLostContactSeconds += stateDuration;
                }
            }
        }
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
            if (RlOneVsOneAgent.RequiresPolicyControl(ship) &&
                !RlPlayerDerivedActionReplay.IsScriptedSide(ship.Level, ship.Side))
            {
                _policyEligibleShipIds[sideIndex].Add(ship.Id);
                if (ship.IsRlPolicyControlled)
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
            if (!RlPlayerDerivedActionReplay.IsScriptedSide(level, side) &&
                RlOneVsOneAgent.RequiresPolicyControl(ship) && !ship.IsRlPolicyControlled)
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
            if (ship != null && !ship.IsDead && HasPersistentFleetValue(ship))
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

    private void ApplyImmediateTsvReward(int side, float reward, string source)
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
        AddRewardSource(sideIndex, source, emittedReward);
        TsvRewardOccurred?.Invoke(_level, side, emittedReward);
    }

    private void ApplyEconomicReward(int side, float reward, string source)
    {
        if (reward <= 0f)
        {
            return;
        }

        if (side == ConfigData.Configuration.BeeSide)
        {
            _beeEconomicRewardThisEpisode += reward;
        }
        else if (side == ConfigData.Configuration.HumanSide)
        {
            _humanEconomicRewardThisEpisode += reward;
        }
        else
        {
            return;
        }

        AddRewardSource(side == ConfigData.Configuration.BeeSide ? 0 : 1, source, reward);
        EconomicRewardOccurred?.Invoke(_level, side, reward);
    }

    private void AddRewardSource(int sideIndex, string source, float reward)
    {
        _rewardSources[sideIndex].TryGetValue(source, out float previous);
        _rewardSources[sideIndex][source] = previous + reward;
    }

    private Dictionary<string, object> BuildUnifiedSideData(
        bool bee,
        EpisodeResult result,
        Dictionary<string, object> diagnostics)
    {
        int sideIndex = bee ? 0 : 1;
        var reward = new Dictionary<string, object>();
        foreach (KeyValuePair<string, float> source in _rewardSources[sideIndex])
        {
            reward[source.Key] = source.Value;
        }
        reward["terminal"] = bee ? result.BeeTerminalReward : result.HumanTerminalReward;
        reward["time"] = bee ? result.BeeTimeReward : result.HumanTimeReward;
        reward["retained_mining"] = bee ? result.BeeRetainedMiningReward : result.HumanRetainedMiningReward;

        var combat = new Dictionary<string, object>
        {
            ["shots"] = bee ? _beeShotsThisEpisode : _humanShotsThisEpisode,
            ["turret_hits"] = bee ? _beeTurretHitsThisEpisode : _humanTurretHitsThisEpisode,
            ["turret_damage"] = bee ? _beeTurretDamageThisEpisode : _humanTurretDamageThisEpisode,
            ["static_obstacle_impacts"] = bee ? _beeTurretStaticObstacleImpactsThisEpisode : _humanTurretStaticObstacleImpactsThisEpisode,
            ["special_hits"] = bee ? _beeSpecialHitsThisEpisode : _humanSpecialHitsThisEpisode,
            ["special_damage"] = bee ? _beeSpecialDamageThisEpisode : _humanSpecialDamageThisEpisode,
            ["other_hits"] = bee ? _beeOtherHitsThisEpisode : _humanOtherHitsThisEpisode,
            ["other_damage"] = bee ? _beeOtherDamageThisEpisode : _humanOtherDamageThisEpisode
        };

        var visibility = new Dictionary<string, object>
        {
            ["first_contact_s"] = NullableTime(bee ? _beeFirstContactSeconds : _humanFirstContactSeconds),
            ["first_fire_s"] = NullableTime(bee ? _beeFirstFireSeconds : _humanFirstFireSeconds),
            ["first_hit_s"] = NullableTime(bee ? _beeFirstHitSeconds : _humanFirstHitSeconds),
            ["no_enemy_visible_s"] = bee ? _beeNoEnemyVisibleSeconds : _humanNoEnemyVisibleSeconds,
            ["contact_losses"] = bee ? _beeContactLossCount : _humanContactLossCount,
            ["lost_contact_s"] = bee ? _beeLostContactSeconds : _humanLostContactSeconds
        };

        diagnostics["combat"] = combat;
        diagnostics["aim"] = RlOneVsOneCombatTelemetry.BuildAimData(_level, sideIndex);
        diagnostics["visibility"] = visibility;
        diagnostics["rewards"] = reward;
        diagnostics["retained_mining_tsv"] = bee ? _beeRetainedMinedTsvThisEpisode : _humanRetainedMinedTsvThisEpisode;
        return diagnostics;
    }

    private static object NullableTime(float seconds)
    {
        return seconds < 0f ? null : (object)seconds;
    }

    private void WriteUnifiedEpisodeRecord(
        Level level,
        EpisodeResult result,
        float mapSize)
    {
        Dictionary<string, object> diagnostic = RlOneVsOneEpisodeDiagnostics.BuildEpisodeData(level);
        var bee = (Dictionary<string, object>)diagnostic["bee"];
        var human = (Dictionary<string, object>)diagnostic["human"];
        var elimination = new Dictionary<string, object>();
        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        if (level.State.IsSideKilled(beeSide))
        {
            elimination["bee"] = new Dictionary<string, object>
            {
                ["cause"] = RlOneVsOneEpisodeDiagnostics.GetLastDeathCause(level, 0),
                ["opponent_caused"] = _hasRecordedDeathAttribution[0]
                    ? (object)_lastDeathWasOpponentCaused[0] : null
            };
        }
        if (level.State.IsSideKilled(humanSide))
        {
            elimination["human"] = new Dictionary<string, object>
            {
                ["cause"] = RlOneVsOneEpisodeDiagnostics.GetLastDeathCause(level, 1),
                ["opponent_caused"] = _hasRecordedDeathAttribution[1]
                    ? (object)_lastDeathWasOpponentCaused[1] : null
            };
        }

        RlEpisodeRecordWriter.Write(new Dictionary<string, object>
        {
            ["schema"] = 1,
            ["episode"] = result.EpisodeNumber,
            ["arena"] = GetArenaIndex(),
            ["duration_s"] = result.DurationSeconds,
            ["map_size"] = mapSize,
            ["outcome"] = result.TimedOut ? "timeout"
                : result.WinningSide == 0 ? "draw"
                : result.WinningSide == beeSide ? "bee" : "human",
            ["final_elimination"] = elimination,
            ["environment"] = diagnostic["environment"],
            ["bee"] = BuildUnifiedSideData(true, result, bee),
            ["human"] = BuildUnifiedSideData(false, result, human)
        });
    }

    private int GetRetainedMiningTsvForWinningSide(int side, int winningSide)
    {
        return side == winningSide ? GetRetainableMinedTsv(_level, side) : 0;
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
        FinalizeEnemyVisibilityDiagnostics(durationSeconds);
        float mapSize = RlOneVsOneArenaMapSizeState.GetMapSize(level);
        float beeNoEnemyVisibleFraction = durationSeconds > 0f ? _beeNoEnemyVisibleSeconds / durationSeconds : 0f;
        float humanNoEnemyVisibleFraction = durationSeconds > 0f ? _humanNoEnemyVisibleSeconds / durationSeconds : 0f;
        float beeFirstContactToEnd = _beeFirstContactSeconds >= 0f ? Mathf.Max(0f, durationSeconds - _beeFirstContactSeconds) : -1f;
        float humanFirstContactToEnd = _humanFirstContactSeconds >= 0f ? Mathf.Max(0f, durationSeconds - _humanFirstContactSeconds) : -1f;
        float beeFirstContactToFire = _beeFirstContactSeconds >= 0f && _beeFirstFireSeconds >= _beeFirstContactSeconds
            ? _beeFirstFireSeconds - _beeFirstContactSeconds : -1f;
        float humanFirstContactToFire = _humanFirstContactSeconds >= 0f && _humanFirstFireSeconds >= _humanFirstContactSeconds
            ? _humanFirstFireSeconds - _humanFirstContactSeconds : -1f;

        bool beeNonOpponentElimination =
            !timedOut &&
            winningSide != 0 &&
            winningSide != beeSide &&
            level.State.IsSideKilled(beeSide) &&
            _hasRecordedDeathAttribution[0] &&
            !_lastDeathWasOpponentCaused[0];
        bool humanNonOpponentElimination =
            !timedOut &&
            winningSide != 0 &&
            winningSide != humanSide &&
            level.State.IsSideKilled(humanSide) &&
            _hasRecordedDeathAttribution[1] &&
            !_lastDeathWasOpponentCaused[1];

        float beeTerminal = RlOneVsOneReward.CalculateTerminalReward(
            beeSide, winningSide, timedOut, beeNonOpponentElimination);
        float humanTerminal = RlOneVsOneReward.CalculateTerminalReward(
            humanSide, winningSide, timedOut, humanNonOpponentElimination);
        float beeTimeReward = winningSide == beeSide ? RlOneVsOneReward.CalculateTimePenalty(durationSeconds) : 0f;
        float humanTimeReward = winningSide == humanSide ? RlOneVsOneReward.CalculateTimePenalty(durationSeconds) : 0f;

        _beeRetainedMinedTsvThisEpisode = GetRetainedMiningTsvForWinningSide(beeSide, winningSide);
        _humanRetainedMinedTsvThisEpisode = GetRetainedMiningTsvForWinningSide(humanSide, winningSide);
        float beeRetainedMiningReward = RlOneVsOneReward.CalculateEconomicValueReward(
            _beeRetainedMinedTsvThisEpisode, _beeStartingTsv);
        float humanRetainedMiningReward = RlOneVsOneReward.CalculateEconomicValueReward(
            _humanRetainedMinedTsvThisEpisode, _humanStartingTsv);
        _beeEconomicRewardThisEpisode += beeRetainedMiningReward;
        _humanEconomicRewardThisEpisode += humanRetainedMiningReward;

        EpisodeResult result = new EpisodeResult(
            _episodeNumber, _beeTeamId, _humanTeamId, winningSide, timedOut, durationSeconds,
            _beeStartingTsv, beeFinalTsv, _humanStartingTsv, humanFinalTsv,
            _beeShotsThisEpisode, _beeHitsThisEpisode, _beeDamageThisEpisode,
            _humanShotsThisEpisode, _humanHitsThisEpisode, _humanDamageThisEpisode,
            beeTerminal, _beeTsvRewardThisEpisode, beeTimeReward,
            _beeEconomicRewardThisEpisode, beeRetainedMiningReward,
            humanTerminal, _humanTsvRewardThisEpisode, humanTimeReward,
            _humanEconomicRewardThisEpisode, humanRetainedMiningReward);
        LastEpisodeResult = result;

        // Diagnostics must never make an otherwise valid training episode fail.
        try
        {
            WriteUnifiedEpisodeRecord(level, result, mapSize);
        }
        catch (Exception exception)
        {
            if (!_trainingDiagnosticWriteWarningEmitted)
            {
                _trainingDiagnosticWriteWarningEmitted = true;
                Debug.LogWarning("RL episode record failed: " + exception.Message);
            }
        }
        FlushCommunicationDiagnostics();
        RlOneVsOneEpisodeDiagnostics.End(level);

        _episodeActive = false;
        _discoveryRewardsReady = false;
        EpisodeEnded?.Invoke(level, result);

        // Terminal MA-POCA delivery resets participating Agents synchronously. Clear the randomized
        // coordinate frame only after every terminal handler has run so those resets cannot create
        // competing next-episode frames while this event is still being dispatched.
        RlPolicyCoordinateFrame.EndEpisode(level);
    }

    private void FlushCommunicationDiagnostics()
    {
        if (_communicationTrace.Length == 0)
        {
            return;
        }

        string payload = _communicationTrace.ToString();
        _communicationTrace.Clear();
        WriteCommunicationDiagnostic(payload);
    }

    private static void WriteCommunicationDiagnostic(string message)
    {
        string logRoot = Environment.GetEnvironmentVariable("BEES_TRAINING_LOG_DIR");
        if (string.IsNullOrWhiteSpace(logRoot))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(logRoot);
            int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            string payload = message.EndsWith("\n", StringComparison.Ordinal)
                ? message
                : message + Environment.NewLine;
            int incomingBytes = TrainingDiagnosticEncoding.GetByteCount(payload);
            lock (CommunicationDiagnosticLogLock)
            {
                string path = Path.Combine(
                    logRoot,
                    $"BeesCommunication-{processId}-{_communicationDiagnosticSegment:D5}.log");
                if (File.Exists(path) &&
                    new FileInfo(path).Length + incomingBytes > CommunicationDiagnosticMaxBytes)
                {
                    _communicationDiagnosticSegment++;
                    path = Path.Combine(
                        logRoot,
                        $"BeesCommunication-{processId}-{_communicationDiagnosticSegment:D5}.log");
                }
                File.AppendAllText(path, payload, TrainingDiagnosticEncoding);
            }
        }
        catch (Exception exception)
        {
            if (!_communicationDiagnosticWriteWarningEmitted)
            {
                _communicationDiagnosticWriteWarningEmitted = true;
                Debug.LogWarning($"RL communication diagnostic sidecar failed; communication tracing is disabled until the next successful write: {exception.Message}");
            }
        }
    }

    private int GetArenaIndex()
    {
        if (_stage == null || _level == null || _stage.Levels == null)
        {
            return -1;
        }

        for (int arenaIndex = 0; arenaIndex < _stage.Levels.Count; arenaIndex++)
        {
            if (_stage.Levels[arenaIndex] == _level)
            {
                return arenaIndex;
            }
        }
        return -1;
    }

    private float ElapsedEpisodeSeconds => Mathf.Max(0f, Time.time - _episodeStartedAt);

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

    internal static int GetCoordinatorCountForTests()
    {
        int count = 0;
        foreach (RlOneVsOneEpisodeCoordinator coordinator in Coordinators.Values)
        {
            if (coordinator != null)
            {
                count++;
            }
        }
        return count;
    }

    internal static void ResetForTests()
    {
        Coordinators.Clear();
        LastEpisodeResult = default;
    }
}
