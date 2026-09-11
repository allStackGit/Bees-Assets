using Assets.Scripts;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

/// <summary>
/// Creates private deterministic RNG seeds for RL scenario samplers. ML-Agents seeds
/// UnityEngine.Random when its communicator initializes. We capture one process root from that state,
/// then derive stable per-arena/per-stream seeds so asynchronous arena timing cannot swap RNG streams.
/// </summary>
internal static class RlOneVsOneScenarioSeed
{
    internal const int MatchupStreamSalt = 0x4D415443; // "MATC"
    internal const int MapSizeStreamSalt = 0x4D415053; // "MAPS"

    private static int? _rootSeed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _rootSeed = null;
    }

    internal static void EnsureMlAgentsSeedIsInitialized()
    {
        _ = Academy.Instance;
    }

    internal static int Create(Level level, int streamSalt)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }

        return Derive(GetRootSeed(), GetArenaIndex(level), streamSalt);
    }

    private static int GetRootSeed()
    {
        EnsureMlAgentsSeedIsInitialized();
        if (!_rootSeed.HasValue)
        {
            // Academy initialization applies the trainer-provided seed to UnityEngine.Random. Capture
            // exactly one value immediately afterwards; scenario sampling then uses only private RNGs.
            _rootSeed = UnityEngine.Random.Range(0, int.MaxValue);
        }
        return _rootSeed.Value;
    }

    private static int GetArenaIndex(Level level)
    {
        IReadOnlyList<Level> levels = level.Stage?.Levels;
        if (levels != null)
        {
            for (int index = 0; index < levels.Count; index++)
            {
                if (ReferenceEquals(levels[index], level))
                {
                    return index;
                }
            }
        }

        throw new InvalidOperationException(
            "RL scenario seed requested before the Level was registered with its Stage.");
    }

    internal static int Derive(int rootSeed, int arenaIndex, int streamSalt)
    {
        if (arenaIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arenaIndex));
        }

        unchecked
        {
            int hash = rootSeed;
            hash = (hash * 397) ^ arenaIndex;
            hash = (hash * 397) ^ streamSalt;
            return hash & int.MaxValue;
        }
    }
}

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
            int seed = RlOneVsOneScenarioSeed.Create(level, RlOneVsOneScenarioSeed.MatchupStreamSalt);
            selector = new RlOneVsOneEpisodeMatchupSelector(options, seed);
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
