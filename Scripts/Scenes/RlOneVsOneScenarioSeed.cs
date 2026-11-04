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
    internal const int EnvironmentStreamSalt = 0x454E5652; // "ENVR"

    private static int? _rootSeed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _rootSeed = null;
    }

    internal static int Create(string[] args)
    {
        if (!RlOneVsOneEvaluationSideChannel.IsEvaluationMode(args))
        {
            // Ordinary training stays stochastic unless research explicitly
            // selects a process root. Separate per-arena streams still derive
            // deterministically from this root; distributed execution order
            // need not become deterministic.
            int? requestedSeed = RlOneVsOneTrainingOptions.Parse(args).ScenarioSeed;
            return requestedSeed.HasValue
                ? DeriveProcessRoot(requestedSeed.Value, args)
                : Guid.NewGuid().GetHashCode();
        }

        return UnityEngine.Random.Range(0, int.MaxValue);
    }

    // ML-Agents launches each Unity worker with an individual port. A single
    // configured seed must not give every process an identical random stream;
    // use stable machine/port identity to keep parallel environments diverse
    // while permitting repeatable topologies across independent training runs.
    internal static int DeriveProcessRoot(int seed, string[] args)
    {
        int port = 0;
        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i] ?? string.Empty;
                if (argument.Equals("--mlagents-port", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out port);
                    break;
                }
                const string prefix = "--mlagents-port=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(argument.Substring(prefix.Length), out port);
                    break;
                }
            }
        }

        if (port <= 0)
        {
            // Do not silently reuse an identical stream across processes whose
            // stable worker identity is unavailable.
            Debug.LogWarning("RL scenario seed requested without an ML-Agents port: " +
                "worker scenarios cannot be reproduced exactly.");
            return Derive(seed, Guid.NewGuid().GetHashCode() & int.MaxValue,
                EnvironmentStreamSalt);
        }

        int hostHash = 23;
        unchecked
        {
            foreach (char value in Environment.MachineName.ToUpperInvariant())
            {
                hostHash = (hostHash * 31) + value;
            }
        }
        return Derive(Derive(seed, hostHash & int.MaxValue, EnvironmentStreamSalt),
            port, IdentityStreamSalt);
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
    }
}
