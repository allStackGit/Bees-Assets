using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using UnityEngine;

/// <summary>
/// Applies the dedicated RL health curriculum when a ship is set up. This is
/// deliberately not a per-frame component: pooled Ship.Setup restores authored
/// health every episode, and all initial and mid-episode ships pass through it.
/// </summary>
internal static class RlOneVsOneTrainingDurabilityGuard
{
    internal const float TrainingHealthFraction = RlOneVsOneTrainingOptions.DefaultHealthRatio;

    internal static int CalculateTrainingHealth(int originalHealth)
    {
        return CalculateTrainingHealth(originalHealth, RlOneVsOneTrainingBootstrap.CurrentHealthRatio);
    }

    internal static int CalculateTrainingHealth(int originalHealth, float healthRatio)
    {
        if (originalHealth <= 0)
        {
            return 0;
        }

        return Mathf.Max(1, Mathf.CeilToInt(originalHealth * healthRatio));
    }

    internal static void ApplyTrainingDurability(Ship ship)
    {
        if (ship == null || ship.OriginalHealth <= 0)
        {
            return;
        }

        int trainingHealth = CalculateTrainingHealth(ship.OriginalHealth);
        bool durabilityChanged = ship.MaxHealth != trainingHealth || ship.Health > trainingHealth;
        ship.MaxHealth = trainingHealth;

        // Keep authored stats intact for the next pooled episode. This runs only
        // at setup, so damage and healing proceed normally during combat.
        if (ship.Health > trainingHealth)
        {
            ship.Health = trainingHealth;
        }

        // Ship.Setup draws the health bar before applying the curriculum. Refresh
        // it if scaling changed the health displayed by a rendered training scene.
        if (durabilityChanged)
        {
            ship.UpdateHealthBar();
        }
    }
}
