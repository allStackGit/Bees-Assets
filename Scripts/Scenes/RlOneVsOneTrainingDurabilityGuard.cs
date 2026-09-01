using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// First curriculum step for the dedicated RL 1v1 proof. Ships keep their authored combat stats,
/// weapons, TSV and observations, but begin each duel with 25% of their normal durability so the
/// already-learned hit behavior can reach terminal win/loss rewards frequently enough to learn a
/// complete attack sequence. MaxHealth is reduced with Health so normalized health and TSV damage
/// calculations continue to treat the reduced starting durability as a full-health ship.
/// </summary>
[DefaultExecutionOrder(-4000)]
internal sealed class RlOneVsOneTrainingDurabilityGuard : MonoBehaviour
{
    internal const float TrainingHealthFraction = 0.25f;

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
        if (originalHealth <= 0)
        {
            return 0;
        }

        return Mathf.Max(1, Mathf.CeilToInt(originalHealth * TrainingHealthFraction));
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
        // therefore reapplies the curriculum each episode without changing the authored stat or the
        // ordinary game. During combat Health is already <= trainingHealth and is left untouched.
        if (ship.Health > trainingHealth)
        {
            ship.Health = trainingHealth;
        }
    }
}
