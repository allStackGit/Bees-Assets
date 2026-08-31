using Assets.Scripts.Levels;
using System;
using UnityEngine;

/// <summary>
/// Owns episode boundaries and reward reporting for the dedicated first RL proof.
/// It runs before the ordinary Level update so a terminal state can be measured before
/// the legacy neural-training reset path tears the episode down.
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

        if (!_episodeActive)
        {
            TryBeginEpisode(currentLevel);
            return;
        }

        if (_level != currentLevel)
        {
            _episodeActive = false;
            TryBeginEpisode(currentLevel);
            return;
        }

        if (currentLevel.State.GameOver)
        {
            int winningSide = DetermineWinner(currentLevel);
            CompleteEpisode(currentLevel, winningSide, false);
            return;
        }

        float elapsedSeconds = Mathf.Max(0f, Time.time - _episodeStartedAt);
        if (elapsedSeconds >= RlOneVsOneTrainingBootstrap.TrainingTimeoutSeconds)
        {
            CompleteEpisode(currentLevel, 0, true);

            // Reset before Level.Update sees its legacy timeout timer. This keeps timeout handling on
            // the same fast episode-reset path as elimination and guarantees the no-winner reward is
            // emitted before the old episode is torn down.
            currentLevel.ResetLevel(true);
        }
    }

    private void TryBeginEpisode(Level level)
    {
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
        float timeReward = RlOneVsOneReward.CalculateTimePenalty(durationSeconds);

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
            timeReward,
            humanTerminal,
            humanTsv,
            timeReward);

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
