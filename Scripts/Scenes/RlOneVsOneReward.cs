using System;
using UnityEngine;

/// <summary>
/// Reward definition for the dedicated RL combat proof.
/// Terminal victory is deliberately much larger than all positive non-terminal shaping.
/// </summary>
internal static class RlOneVsOneReward
{
    internal const float WinReward = 10f;
    internal const float LossReward = -10f;
    internal const float TsvRewardScale = 1f;
    internal const float MaximumEpisodeTimePenalty = 0.1f;

    // Positive shaping is transformed through an asymptotic bound rather than hard-clamped. Every
    // finite useful outcome therefore keeps a positive learning signal while the entire episode's
    // positive non-terminal shaping remains strictly below the value of winning the battle.
    internal const float MaximumPositiveShapingReward = 2f;

    // Discovery is intentionally a small fraction of combat outcome shaping. Static categories are
    // normalized against the value present when the episode begins. Collision asteroids can spawn
    // indefinitely, so they use a convergent sequence instead of an episode-start denominator.
    internal const float EnemyShipDiscoveryBudget = 0.06f;
    internal const float MiningAsteroidDiscoveryBudget = 0.015f;
    internal const float StaticObstacleDiscoveryBudget = 0.015f;
    internal const float MapObjectDiscoveryBudget = 0.01f;
    internal const float CollisionAsteroidDiscoveryBudget = 0.025f;

    /// <summary>
    /// Converts a real positive TSV-valued outcome into immediate shaping. This is shared by enemy
    /// damage, restored health, mined resources and successful ship preservation so all of those
    /// outcomes use the same value scale instead of rewarding the attempted action itself.
    /// </summary>
    internal static float CalculateTsvValueReward(int tsvValue, int combinedStartingTsv)
    {
        float denominator = Mathf.Max(1, combinedStartingTsv);
        return (Mathf.Max(0, tsvValue) / denominator) * TsvRewardScale;
    }

    internal static float CalculateTsvLossReward(int tsvLost, int combinedStartingTsv)
    {
        return CalculateTsvValueReward(tsvLost, combinedStartingTsv);
    }

    /// <summary>
    /// Gives a first-discovery reward proportional to this object's strategic value. If the entire
    /// episode-start category is eventually discovered, its raw rewards sum to at most the category
    /// budget. The discovered value also participates in the denominator so an unexpected spawned
    /// object can never yield more than the whole category budget by itself.
    /// </summary>
    internal static float CalculateStaticDiscoveryReward(
        int discoveryValue,
        int episodeStartCategoryValue,
        float categoryBudget)
    {
        int value = Mathf.Max(0, discoveryValue);
        if (value <= 0 || categoryBudget <= 0f)
        {
            return 0f;
        }

        float denominator = Mathf.Max(value, Mathf.Max(1, episodeStartCategoryValue));
        return Mathf.Max(0f, categoryBudget) * value / denominator;
    }

    /// <summary>
    /// Collision asteroids are dynamic discoveries, so the Nth first-sighting receives a term from
    /// 1/((n+1)(n+2)). Those terms sum to one even for infinitely many spawns. Size scales each term
    /// monotonically but remains below one, preserving the overall collision-asteroid budget while
    /// ensuring every finite discovery still has positive value.
    /// </summary>
    internal static float CalculateCollisionAsteroidDiscoveryReward(int sizeClass, int discoveryIndex)
    {
        int size = Mathf.Max(0, sizeClass);
        if (size <= 0)
        {
            return 0f;
        }

        int index = Mathf.Max(0, discoveryIndex);
        float sequenceWeight = 1f / ((index + 1f) * (index + 2f));
        float sizeWeight = size / (size + 1f);
        return CollisionAsteroidDiscoveryBudget * sizeWeight * sequenceWeight;
    }

    /// <summary>
    /// Maps cumulative raw positive shaping onto [0, MaximumPositiveShapingReward). The rational
    /// form approaches the limit smoothly and is also used to derive an exact positive increment,
    /// avoiding a subtract-two-nearly-equal-floats cutoff late in a long episode.
    /// </summary>
    internal static double CalculateBoundedPositiveShapingReward(double cumulativeRawPositiveReward)
    {
        double raw = Math.Max(0d, cumulativeRawPositiveReward);
        double maximum = MaximumPositiveShapingReward;
        return maximum * raw / (maximum + raw);
    }

    /// <summary>
    /// Exact difference of the bounded-positive function before and after one new raw reward. For
    /// positive finite inputs this remains positive without ever consuming a hard episode budget.
    /// </summary>
    internal static double CalculateBoundedPositiveShapingIncrement(
        double cumulativeRawPositiveReward,
        double additionalRawPositiveReward)
    {
        double raw = Math.Max(0d, cumulativeRawPositiveReward);
        double added = Math.Max(0d, additionalRawPositiveReward);
        if (added <= 0d)
        {
            return 0d;
        }

        double maximum = MaximumPositiveShapingReward;
        return maximum * maximum * added /
               ((maximum + raw) * (maximum + raw + added));
    }

    /// <summary>
    /// Calculates the equivalent net full-episode TSV exchange. This remains useful as a reference
    /// and for validation; live combat training emits the same shaping incrementally as hits occur.
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
    /// Applies a very small continuous cost for elapsed battle time. A complete configured episode
    /// costs only 0.1 reward, so speed can break otherwise similar victories without encouraging
    /// sacrificing a ship merely to finish a little sooner.
    /// </summary>
    internal static float CalculateTimePenalty(float elapsedSeconds)
    {
        float timeoutSeconds = Mathf.Max(1f, RlOneVsOneTrainingBootstrap.CurrentTimeoutSeconds);
        float normalizedElapsed = Mathf.Clamp01(elapsedSeconds / timeoutSeconds);
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
