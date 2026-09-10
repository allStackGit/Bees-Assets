using Assets.Scripts;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns sampled matchup state per training Level. A selector contains both the prepared matchup and
/// its recent-result history, so keeping one selector per Level prevents asynchronously completing
/// arenas from overwriting each other's prepared matchup or attributing outcomes to the wrong pair.
/// </summary>
internal static class RlOneVsOnePerArenaMatchups
{
    private static readonly Dictionary<Level, RlOneVsOneEpisodeMatchupSelector> Selectors =
        new Dictionary<Level, RlOneVsOneEpisodeMatchupSelector>();

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
        GetSelector(level).PrepareEpisode();
    }

    internal static ConfigData.ShipTypes GetShipType(Level level, int side, int shipIndex)
    {
        return GetSelector(level).GetShipType(side, shipIndex);
    }

    private static RlOneVsOneEpisodeMatchupSelector GetSelector(Level level)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }

        if (!Selectors.TryGetValue(level, out RlOneVsOneEpisodeMatchupSelector selector))
        {
            RlOneVsOneTrainingOptions options = RlOneVsOneTrainingOptions.Parse(Environment.GetCommandLineArgs());
            selector = new RlOneVsOneEpisodeMatchupSelector(options);
            Selectors.Add(level, selector);
        }
        return selector;
    }

    private static void HandleEpisodeEnded(Level level, RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (level != null && Selectors.TryGetValue(level, out RlOneVsOneEpisodeMatchupSelector selector))
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
    }
}
