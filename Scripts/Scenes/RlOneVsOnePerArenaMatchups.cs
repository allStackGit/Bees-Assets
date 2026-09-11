using Assets.Scripts;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns sampled matchup state per training Level. A selector contains both the prepared matchup and
/// its recent-result history, so keeping one selector per Level prevents asynchronously completing
/// arenas from overwriting each other's prepared matchup or attributing outcomes to the wrong pair.
/// Player-derived adversarial pressure wraps that selector per arena and reserves only its explicitly
/// configured bounded fraction of episodes.
/// </summary>
internal static class RlOneVsOnePerArenaMatchups
{
    private static readonly Dictionary<Level, RlOneVsOneAdversarialMatchupSelector> Selectors =
        new Dictionary<Level, RlOneVsOneAdversarialMatchupSelector>();

    static RlOneVsOnePerArenaMatchups()
    {
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetForSceneLoad()
    {
        Selectors.Clear();
    }

    internal static void PrepareEpisode(Level level)
    {
        RlOneVsOneAdversarialMatchupSelector selector = GetSelector(level);
        selector.PrepareEpisode();
        RlPlayerDerivedPressureTelemetry.RecordPrepared(level, selector.CurrentPressureTag);
    }

    internal static ConfigData.ShipTypes GetShipType(Level level, int side, int shipIndex)
    {
        return GetSelector(level).GetShipType(side, shipIndex);
    }

    private static RlOneVsOneAdversarialMatchupSelector GetSelector(Level level)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }

        if (!Selectors.TryGetValue(level, out RlOneVsOneAdversarialMatchupSelector selector))
        {
            string[] args = Environment.GetCommandLineArgs();
            RlOneVsOneTrainingOptions options = RlOneVsOneTrainingOptions.Parse(args);
            IReadOnlyList<RlPlayerDerivedAdversarialScenario> playerDerivedScenarios =
                RlPlayerDerivedAdversarialPressure.Parse(args, options);
            selector = new RlOneVsOneAdversarialMatchupSelector(
                options,
                RlOneVsOneScenarioSeed.Create(args),
                playerDerivedScenarios);
            Selectors.Add(level, selector);
        }
        return selector;
    }

    private static void HandleEpisodeEnded(Level level, RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (level != null && Selectors.TryGetValue(level, out RlOneVsOneAdversarialMatchupSelector selector))
        {
            selector.RecordEpisodeOutcome(result.WinningSide, result.TimedOut);
        }
    }

    internal static int GetSelectorCountForTests()
    {
        return Selectors.Count;
    }

    internal static void ResetForTests()
    {
        Selectors.Clear();
        RlPlayerDerivedPressureTelemetry.ResetForTests();
    }
}