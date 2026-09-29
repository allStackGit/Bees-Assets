using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Gives every ship that participates in an RL observation a stable one-float identity for the
/// current episode. Identities are assigned from a randomly permuted scalar code space so repeated
/// values mean "the same ship" without making spawn order or runtime Entity.Id numerically meaningful.
/// </summary>
internal static class RlEpisodeShipIdentity
{
    // 2^23 evenly spaced values across [-1, 1] remain distinct as IEEE-754 single-precision floats.
    // This is representational capacity, not a gameplay/team-size limit.
    internal const int ScalarBitDepth = 23;
    internal const int ScalarValueCount = 1 << ScalarBitDepth;
    private const int ScalarMask = ScalarValueCount - 1;

    private sealed class EpisodeIdentityState
    {
        internal readonly Dictionary<long, int> Ordinals = new Dictionary<long, int>();
        internal readonly int Multiplier;
        internal readonly int Offset;
        internal int NextOrdinal;

        internal EpisodeIdentityState(System.Random random)
        {
            Multiplier = random.Next(1, ScalarValueCount) | 1;
            Offset = random.Next(ScalarValueCount);
        }
    }

    private static readonly Dictionary<Level, EpisodeIdentityState> EpisodeStates =
        new Dictionary<Level, EpisodeIdentityState>();
    private static readonly Dictionary<Level, System.Random> IdentityRandoms =
        new Dictionary<Level, System.Random>();

    static RlEpisodeShipIdentity()
    {
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetForSceneLoad()
    {
        EpisodeStates.Clear();
        IdentityRandoms.Clear();
    }

    internal static float GetObservation(Ship ship)
    {
        if (ship == null || ship.Level == null)
        {
            return 0f;
        }

        Level level = ship.Level;
        if (!EpisodeStates.TryGetValue(level, out EpisodeIdentityState state))
        {
            state = new EpisodeIdentityState(GetIdentityRandom(level));
            EpisodeStates.Add(level, state);
        }

        if (!state.Ordinals.TryGetValue(ship.Id, out int ordinal))
        {
            if (state.NextOrdinal >= ScalarValueCount)
            {
                throw new InvalidOperationException(
                    $"RL episode exhausted the {ScalarValueCount} distinct single-float ship identities.");
            }

            ordinal = state.NextOrdinal++;
            state.Ordinals.Add(ship.Id, ordinal);
        }

        return EncodeOrdinal(ordinal, state.Multiplier, state.Offset);
    }

    internal static float EncodeOrdinal(int ordinal, int multiplier, int offset)
    {
        if (ordinal < 0 || ordinal >= ScalarValueCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        if (multiplier <= 0 || multiplier >= ScalarValueCount || (multiplier & 1) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Permutation multiplier must be odd and inside the scalar code space.");
        }
        if (offset < 0 || offset >= ScalarValueCount)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        int permuted = (int)(((long)ordinal * multiplier + offset) & ScalarMask);
        return -1f + 2f * permuted / ScalarMask;
    }

    private static System.Random GetIdentityRandom(Level level)
    {
        if (!IdentityRandoms.TryGetValue(level, out System.Random random))
        {
            random = new System.Random(Guid.NewGuid().GetHashCode());
            IdentityRandoms.Add(level, random);
        }
        return random;
    }

    private static void HandleEpisodeEnded(Level level, RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (level != null)
        {
            EpisodeStates.Remove(level);
        }
    }

    internal static void ResetForTests()
    {
        EpisodeStates.Clear();
        IdentityRandoms.Clear();
    }
}
