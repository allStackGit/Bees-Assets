using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Creates private RNG seeds for RL scenario samplers. Ordinary training keeps a fresh process root;
/// authoritative evaluation derives that root from ML-Agents-seeded Unity randomness. Per-arena and
/// per-stream seeds are then derived from the single root so asynchronous arena timing cannot swap or
/// consume another arena's matchup/map random sequence.
/// </summary>
internal static class RlOneVsOneScenarioSeed
{
    internal const int MatchupStreamSalt = 0x4D415443; // "MATC"
    internal const int MapSizeStreamSalt = 0x4D415053; // "MAPS"
    internal const int IdentityStreamSalt = 0x4944454E; // "IDEN"
    internal const int CoordinateFrameStreamSalt = 0x4346524D; // "CFRM"
    internal const int SpawnPositionStreamSalt = 0x5350574E; // "SPWN"
    internal const int FacingStreamSalt = 0x46414345; // "FACE"

    private static int? _rootSeed;
    private static readonly Dictionary<Level, Dictionary<int, System.Random>> RandomStreams =
        new Dictionary<Level, Dictionary<int, System.Random>>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _rootSeed = null;
        RandomStreams.Clear();
    }

    internal static int Create(string[] args)
    {
        if (!RlOneVsOneEvaluationSideChannel.IsEvaluationMode(args))
        {
            return Guid.NewGuid().GetHashCode();
        }

        return UnityEngine.Random.Range(0, int.MaxValue);
    }

    internal static int Create(Level level, int streamSalt, string[] args = null)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }

        return Derive(GetRootSeed(args ?? Environment.GetCommandLineArgs()), GetArenaIndex(level), streamSalt);
    }

    private static int GetRootSeed(string[] args)
    {
        if (!_rootSeed.HasValue)
        {
            _rootSeed = Create(args);
        }
        return _rootSeed.Value;
    }

    internal static System.Random GetRandom(Level level, int streamSalt, string[] args = null)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }

        if (!RandomStreams.TryGetValue(level, out Dictionary<int, System.Random> streams))
        {
            streams = new Dictionary<int, System.Random>();
            RandomStreams.Add(level, streams);
        }
        if (!streams.TryGetValue(streamSalt, out System.Random random))
        {
            int seed = Derive(
                GetRootSeed(args ?? Environment.GetCommandLineArgs()),
                GetArenaIndex(level),
                streamSalt);
            random = new System.Random(seed);
            streams.Add(streamSalt, random);
        }
        return random;
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

    internal static void SetRootSeedForTests(int rootSeed)
    {
        _rootSeed = rootSeed;
    }

    internal static void ResetForTests()
    {
        _rootSeed = null;
        RandomStreams.Clear();
    }
}
