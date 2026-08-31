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
    /// Rewards net TSV exchanged since the previous sample. Normalizing by the combined starting
    /// TSV bounds the full-episode shaping to roughly one point in an ordinary two-ship duel.
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
    /// costs only 0.1 reward, so speed can break otherwise similar outcomes without encouraging
    /// sacrificing a ship merely to finish a little sooner.
    /// </summary>
    internal static float CalculateTimePenalty(float elapsedSeconds)
    {
        float normalizedElapsed = Mathf.Clamp01(elapsedSeconds / RlOneVsOneTrainingBootstrap.TrainingTimeoutSeconds);
        return -normalizedElapsed * MaximumEpisodeTimePenalty;
    }

    internal static float CalculateTerminalReward(int side, int winningSide)
    {
        if (winningSide == 0)
        {
            return 0f;
        }
        return side == winningSide ? WinReward : LossReward;
    }
}
