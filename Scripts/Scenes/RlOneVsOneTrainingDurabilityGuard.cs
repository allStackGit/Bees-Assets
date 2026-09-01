using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Training-only durability curriculum for the dedicated RL combat scene. Ships keep their authored
/// combat stats, weapons, TSV and observations, but MaxHealth and starting Health are scaled by the
/// configured health ratio. MaxHealth is reduced with Health so normalized health and TSV damage
/// calculations continue to treat the configured starting durability as a full-health ship.
/// </summary>
[DefaultExecutionOrder(-4000)]
internal sealed class RlOneVsOneTrainingDurabilityGuard : MonoBehaviour
{
    // Retained as the stable no-argument curriculum default and for regression compatibility.
    internal const float TrainingHealthFraction = RlOneVsOneTrainingOptions.DefaultHealthRatio;

    private Stage _stage;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AttachToDedicatedTrainingScene()
    {
        if (!RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = Object.FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            Debug.LogError("RL 1v1 training scene cannot attach its durability curriculum because no Stage exists.");
            return;
        }

        RlOneVsOneTrainingDurabilityGuard guard = stage.GetComponent<RlOneVsOneTrainingDurabilityGuard>();
        if (guard == null)
        {
            guard = stage.gameObject.AddComponent<RlOneVsOneTrainingDurabilityGuard>();
        }
        guard._stage = stage;
    }

    private void FixedUpdate()
    {
        if (!RlOneVsOneTrainingBootstrap.IsActiveFor(_stage))
        {
            return;
        }

        Level level = _stage.PrimaryLevel;
        if (level == null || level.State == null)
        {
            return;
        }

        List<Ship> ships = level.State.GetShips();
        for (int i = 0; i < ships.Count; i++)
        {
            ApplyTrainingDurability(ships[i]);
        }
    }

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
        ship.MaxHealth = trainingHealth;

        // Ship.Setup restores Health to OriginalHealth on every pooled episode reset. Clamping here
        // therefore reapplies the selected curriculum each episode without changing the authored stat
        // or ordinary game. During combat Health is already <= trainingHealth and is left untouched.
        if (ship.Health > trainingHealth)
        {
            ship.Health = trainingHealth;
        }
    }
}
