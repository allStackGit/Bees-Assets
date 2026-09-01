using UnityEngine;

/// <summary>
/// Reward definition for the first Wasp-vs-Gunship learning proof.
/// Terminal victory is deliberately an order of magnitude larger than all shaping.
/// </summary>
internal static class RlOneVsOneReward
{
    internal const float WinReward = 10f;
    internal const float LossReward = -10f;
    internal const float TsvRewardScale = 1f;
    internal const float MaximumEpisodeTimePenalty = 0.1f;

    /// <summary>
    /// Converts one real combat TSV loss into immediate shaping. Normalizing by the combined
    /// starting TSV keeps the sum of all hit rewards on the same scale as the original
    /// full-episode TSV exchange reward while making credit assignment local to the hit.
    /// </summary>
    internal static float CalculateTsvLossReward(int tsvLost, int combinedStartingTsv)
    {
        float denominator = Mathf.Max(1, combinedStartingTsv);
        return (Mathf.Max(0, tsvLost) / denominator) * TsvRewardScale;
    }

    /// <summary>
    /// Calculates the equivalent net full-episode TSV exchange. This remains useful as a reference
    /// and for validation; live training emits the same shaping incrementally as hits occur.
    /// </summary>
    internal static float CalculateTsvDeltaReward(
        int previousFriendlyTsv,
        int currentFriendlyTsv,
        int previousEnemyTsv,
        int currentEnemyTsv,
        int combinedStartingTsv)
    {
        int enemyTsvLost = previousEnemyTsv - currentEnemyTsv;
        int friendlyTsvLost = previousFriendlyTsv - currentFriendlyTsv;
        float denominator = Mathf.Max(1, combinedStartingTsv);
        return ((enemyTsvLost - friendlyTsvLost) / denominator) * TsvRewardScale;
    }

    /// <summary>
    /// Applies a very small continuous cost for elapsed battle time. Even a full two-minute timeout
    /// costs only 0.1 reward, so speed can break otherwise similar victories without encouraging
    /// sacrificing a ship merely to finish a little sooner.
    /// </summary>
    internal static float CalculateTimePenalty(float elapsedSeconds)
    {
        float normalizedElapsed = Mathf.Clamp01(elapsedSeconds / RlOneVsOneTrainingBootstrap.TrainingTimeoutSeconds);
        return -normalizedElapsed * MaximumEpisodeTimePenalty;
    }

    /// <summary>
    /// A timeout or simultaneous no-winner result is a failure for both sides. Otherwise a weak ship
    /// could learn that indefinitely avoiding combat is preferable to accepting a likely loss.
    /// </summary>
    internal static float CalculateTerminalReward(int side, int winningSide)
    {
        if (winningSide == 0)
        {
            return LossReward;
        }
        return side == winningSide ? WinReward : LossReward;
    }
}
