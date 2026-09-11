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
    private static readonly HashSet<Level> PlayerDerivedPressureLevels = new HashSet<Level>();
    private static readonly HashSet<Level> PreparedEpisodes = new HashSet<Level>();

    static RlOneVsOnePerArenaMatchups()
    {
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetForSceneLoad()
    {
        Selectors.Clear();
        PlayerDerivedPressureLevels.Clear();
        PreparedEpisodes.Clear();
    }

    /// <summary>
    /// Prepare exactly one matchup for the current Level episode. Map setup may call this before ship
    /// setup so player-derived tactical geometry can affect the map; the existing ship-setup call is
    /// deliberately retained and becomes an idempotent no-op for the same episode.
    /// </summary>
    internal static void PrepareEpisode(Level level)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }
        if (PreparedEpisodes.Contains(level))
        {
            return;
        }

        RlOneVsOneAdversarialMatchupSelector selector = GetSelector(level);
        selector.PrepareEpisode();
        PreparedEpisodes.Add(level);
        RlPlayerDerivedActionReplay.PrepareEpisode(level);
        if (PlayerDerivedPressureLevels.Contains(level))
        {
            RlPlayerDerivedPressureTelemetry.RecordPrepared(level, selector.CurrentPressureTag);
        }
    }

    internal static ConfigData.ShipTypes GetShipType(Level level, int side, int shipIndex)
    {
        return GetSelector(level).GetShipType(side, shipIndex);
    }

    internal static string GetCurrentPressureTag(Level level)
    {
        if (level == null || !PreparedEpisodes.Contains(level) ||
            !Selectors.TryGetValue(level, out RlOneVsOneAdversarialMatchupSelector selector))
        {
            return null;
        }
        return selector.CurrentPressureTag;
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
            if (playerDerivedScenarios.Count > 0)
            {
                PlayerDerivedPressureLevels.Add(level);
            }
        }
        return selector;
    }

    private static void HandleEpisodeEnded(Level level, RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (level == null)
        {
            return;
        }

        RlPlayerDerivedActionReplay.EndEpisode(level);
        if (Selectors.TryGetValue(level, out RlOneVsOneAdversarialMatchupSelector selector))
        {
            selector.RecordEpisodeOutcome(result.WinningSide, result.TimedOut);
        }
        PreparedEpisodes.Remove(level);
    }

    internal static int GetSelectorCountForTests()
    {
        return Selectors.Count;
    }

    internal static int GetPreparedEpisodeCountForTests()
    {
        return PreparedEpisodes.Count;
    }

    internal static void ResetForTests()
    {
        Selectors.Clear();
        PlayerDerivedPressureLevels.Clear();
        PreparedEpisodes.Clear();
        RlPlayerDerivedPressureTelemetry.ResetForTests();
        RlPlayerDerivedActionReplay.ResetForTests();
    }
}
