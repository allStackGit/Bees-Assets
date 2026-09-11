using System;
using UnityEngine;

/// <summary>
/// Creates private RNG seeds for RL scenario samplers.
///
/// Normal training intentionally preserves the existing independently-randomized streams. The
/// authoritative evaluator instead derives them from UnityEngine.Random after ML-Agents has seeded
/// that RNG during communicator initialization, so separate candidate/baseline processes launched
/// with the same evaluator seed receive the same scenario sequence.
/// </summary>
internal static class RlOneVsOneScenarioSeed
{
    internal static int Create(string[] args)
    {
        if (!RlOneVsOneEvaluationSideChannel.IsEvaluationMode(args))
        {
            return Guid.NewGuid().GetHashCode();
        }

        return UnityEngine.Random.Range(0, int.MaxValue);
    }
}
