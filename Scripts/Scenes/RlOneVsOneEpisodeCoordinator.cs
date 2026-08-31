using Assets.Scripts.Levels;
using System;
using UnityEngine;

/// <summary>
/// Owns episode reward bookkeeping for the dedicated first RL proof.
/// The existing Level lifecycle calls the static completion hooks before it tears an episode down,
/// while this component detects each newly spawned duel and captures its starting TSV/time.
/// </summary>
[DefaultExecutionOrder(-5000)]
internal sealed class RlOneVsOneEpisodeCoordinator : MonoBehaviour
{
    internal readonly struct EpisodeResult
    {
        internal readonly int EpisodeNumber;
        internal readonly int WinningSide;
        internal readonly bool TimedOut;
        internal readonly float DurationSeconds;
        internal readonly int BeeStartingTsv;
        internal readonly int BeeFinalTsv;
        internal readonly int HumanStartingTsv;
        internal readonly int HumanFinalTsv;
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
            int winningSide,
            bool timedOut,
            float durationSeconds,
            int beeStartingTsv,
            int beeFinalTsv,
            int humanStartingTsv,
            int humanFinalTsv,
            float beeTerminalReward,
            float beeTsvReward,
            float beeTimeReward,
            float humanTerminalReward,
            float humanTsvReward,
            float humanTimeReward)
        {
            EpisodeNumber = episodeNumber;
            WinningSide = winningSide;
            TimedOut = timedOut;
            DurationSeconds = durationSeconds;
            BeeStartingTsv = beeStartingTsv;
            BeeFinalTsv = beeFinalTsv;
            HumanStartingTsv = humanStartingTsv;
            HumanFinalTsv = humanFinalTsv;
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
    /// Trainer adapters can subscribe to this without putting trainer-specific dependencies into
    /// the battle scene or the core Level lifecycle.
    /// </summary>
    internal static event Action<EpisodeResult> EpisodeEnded;

    internal static EpisodeResult LastEpisodeResult { get; private set; }

    private static RlOneVsOneEpisodeCoordinator _active;

    private Stage _stage;
    private Level _level;
    private bool _episodeActive;
    private int _episodeNumber;
    private float _episodeStartedAt;
    private int _beeStartingTsv;
    private int _humanStartingTsv;

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

        // Initial TSV is populated while squads are spawned. Waiting for both values keeps the
        // coordinator out of Stage/Level construction ordering and starts timing only once the duel exists.
        if (beeStartingTsv <= 0 || humanStartingTsv <= 0 || level.State.GameOver)
        {
            return;
        }

        _level = level;
        _episodeNumber++;
        _episodeStartedAt = Time.time;
        _beeStartingTsv = beeStartingTsv;
        _humanStartingTsv = humanStartingTsv;
        _episodeActive = true;
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
        int combinedStartingTsv = Mathf.Max(1, _beeStartingTsv + _humanStartingTsv);
        float durationSeconds = Mathf.Clamp(
            Time.time - _episodeStartedAt,
            0f,
            RlOneVsOneTrainingBootstrap.TrainingTimeoutSeconds);

        float beeTerminal = RlOneVsOneReward.CalculateTerminalReward(beeSide, winningSide);
        float humanTerminal = RlOneVsOneReward.CalculateTerminalReward(humanSide, winningSide);
        float beeTsv = RlOneVsOneReward.CalculateTsvDeltaReward(
            _beeStartingTsv,
            beeFinalTsv,
            _humanStartingTsv,
            humanFinalTsv,
            combinedStartingTsv);
        float humanTsv = RlOneVsOneReward.CalculateTsvDeltaReward(
            _humanStartingTsv,
            humanFinalTsv,
            _beeStartingTsv,
            beeFinalTsv,
            combinedStartingTsv);

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
            winningSide,
            timedOut,
            durationSeconds,
            _beeStartingTsv,
            beeFinalTsv,
            _humanStartingTsv,
            humanFinalTsv,
            beeTerminal,
            beeTsv,
            beeTimeReward,
            humanTerminal,
            humanTsv,
            humanTimeReward);

        Debug.Log(
            $"RL 1v1 episode={LastEpisodeResult.EpisodeNumber} winner={winningSide} timeout={timedOut} " +
            $"duration={durationSeconds:F2}s bee_tsv={_beeStartingTsv}->{beeFinalTsv} " +
            $"human_tsv={_humanStartingTsv}->{humanFinalTsv} " +
            $"bee_reward={LastEpisodeResult.BeeTotalReward:F4} " +
            $"human_reward={LastEpisodeResult.HumanTotalReward:F4}");

        _episodeActive = false;
        EpisodeEnded?.Invoke(LastEpisodeResult);
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
