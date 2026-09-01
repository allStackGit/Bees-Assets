using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System;
using UnityEngine;

/// <summary>
/// Owns episode reward bookkeeping and lightweight training diagnostics for the dedicated first RL proof.
/// The existing Level lifecycle calls the static completion hooks before it tears an episode down,
/// while this component detects each newly spawned duel and captures its starting state/time.
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

    /// <summary>
    /// Trainer adapters can subscribe to these without putting trainer-specific dependencies into
    /// the battle scene or the core Level lifecycle. TSV shaping is emitted at the hit that caused it;
    /// the episode event carries terminal/time rewards and diagnostic totals.
    /// </summary>
    internal static event Action<int, float> TsvRewardOccurred;
    internal static event Action<EpisodeResult> EpisodeEnded;

    internal static EpisodeResult LastEpisodeResult { get; private set; }

    private static RlOneVsOneEpisodeCoordinator _active;

    private Stage _stage;
    private Level _level;
    private bool _episodeActive;
    private int _episodeNumber;
    private int _beeTeamId;
    private int _humanTeamId;
    private float _episodeStartedAt;
    private int _beeStartingTsv;
    private int _humanStartingTsv;
    private FleetShip _beeFleetShip;
    private FleetShip _humanFleetShip;
    private int _beeShotsAtStart;
    private int _humanShotsAtStart;
    private int _beeHitsThisEpisode;
    private int _humanHitsThisEpisode;
    private int _beeDamageThisEpisode;
    private int _humanDamageThisEpisode;
    private float _beeTsvRewardThisEpisode;
    private float _humanTsvRewardThisEpisode;

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
    }

    /// <summary>
    /// Returns whether this fixed ML-Agents team instance owns the physical side in the current duel.
    /// Both team IDs alternate between Wasp and Gunship across game episodes so GhostTrainer never
    /// equates one learning team with one faction/ship type.
    /// </summary>
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
    /// Records a real enemy-ship hit after normal damage/TSV calculation has succeeded. Diagnostics
    /// are updated and the exact TSV exchange is rewarded immediately instead of waiting for timeout.
    /// </summary>
    internal static void RecordHit(Ship attacker, Ship target, int damage, int tsvLoss)
    {
        if (_active == null || !_active._episodeActive || attacker == null || target == null ||
            attacker.Level != _active._level || target.Level != _active._level || attacker.Side == target.Side)
        {
            return;
        }

        int appliedDamage = Mathf.Max(0, damage);
        int appliedTsvLoss = Mathf.Max(0, tsvLoss);
        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        if (attacker.Side == beeSide)
        {
            _active._beeHitsThisEpisode++;
            _active._beeDamageThisEpisode += appliedDamage;
        }
        else if (attacker.Side == humanSide)
        {
            _active._humanHitsThisEpisode++;
            _active._humanDamageThisEpisode += appliedDamage;
        }
        else
        {
            return;
        }

        if (appliedTsvLoss <= 0)
        {
            return;
        }

        int combinedStartingTsv = Mathf.Max(1, _active._beeStartingTsv + _active._humanStartingTsv);
        float reward = RlOneVsOneReward.CalculateTsvLossReward(appliedTsvLoss, combinedStartingTsv);
        _active.ApplyImmediateTsvReward(attacker.Side, reward);
        _active.ApplyImmediateTsvReward(target.Side, -reward);
    }

    /// <summary>
    /// Called by Level.LevelOver before the legacy neural-training reset path executes.
    /// </summary>
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

    /// <summary>
    /// Called by Level.LevelTimeOut before SaveAndEnd tears the timed-out episode down.
    /// A timeout deliberately has no winner, making the terminal reward a loss for both sides.
    /// </summary>
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
        if (level == null || level.State == null)
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
        Ship beeShip = FindActiveShip(level, beeSide);
        Ship humanShip = FindActiveShip(level, humanSide);

        // Initial TSV is populated while squads are spawned. Waiting for both values and both ships
        // keeps the coordinator out of Stage/Level construction ordering and starts timing only once
        // the duel and its diagnostic counters actually exist.
        if (beeStartingTsv <= 0 || humanStartingTsv <= 0 || beeShip == null || humanShip == null || level.State.GameOver)
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
        _beeFleetShip = beeShip.FleetShip;
        _humanFleetShip = humanShip.FleetShip;
        _beeShotsAtStart = _beeFleetShip?.ShotsFired ?? 0;
        _humanShotsAtStart = _humanFleetShip?.ShotsFired ?? 0;
        _beeHitsThisEpisode = 0;
        _humanHitsThisEpisode = 0;
        _beeDamageThisEpisode = 0;
        _humanDamageThisEpisode = 0;
        _beeTsvRewardThisEpisode = 0f;
        _humanTsvRewardThisEpisode = 0f;
        _episodeActive = true;
    }

    private void ApplyImmediateTsvReward(int side, float reward)
    {
        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        if (side == beeSide)
        {
            _beeTsvRewardThisEpisode += reward;
        }
        else if (side == humanSide)
        {
            _humanTsvRewardThisEpisode += reward;
        }
        else
        {
            return;
        }

        TsvRewardOccurred?.Invoke(side, reward);
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
        int beeShotsFired = Mathf.Max(0, (_beeFleetShip?.ShotsFired ?? _beeShotsAtStart) - _beeShotsAtStart);
        int humanShotsFired = Mathf.Max(0, (_humanFleetShip?.ShotsFired ?? _humanShotsAtStart) - _humanShotsAtStart);
        float durationSeconds = Mathf.Clamp(
            Time.time - _episodeStartedAt,
            0f,
            RlOneVsOneTrainingBootstrap.TrainingTimeoutSeconds);

        float beeTerminal = RlOneVsOneReward.CalculateTerminalReward(beeSide, winningSide);
        float humanTerminal = RlOneVsOneReward.CalculateTerminalReward(humanSide, winningSide);

        // Time is a tertiary preference among victories. Penalizing a losing side for elapsed time
        // would teach it to die faster; timeouts are already a full terminal loss for both sides.
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

    private static Ship FindActiveShip(Level level, int side)
    {
        var ships = level.State.GetShips(side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship ship = ships[i];
            if (ship != null && !ship.IsDead)
            {
                return ship;
            }
        }
        return null;
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
