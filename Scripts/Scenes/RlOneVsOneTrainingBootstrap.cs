using Assets.Scripts;
using Assets.Scripts.UIComponents;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Configures the first neural-network proof scene as one small, repeatable 1v1 battle.
/// This is intentionally narrow: no Hive Mind commands, no randomized environment dimensions,
/// no mining, and exactly one armed primary-fleet ship per side.
/// </summary>
internal static class RlOneVsOneTrainingBootstrap
{
    internal const string TrainingSceneName = "RL 1v1 Training";
    internal const int TrainingLevelCount = 1;
    internal const int TrainingTimeoutSeconds = 30;
    internal const int TrainingMapIndex = 2; // Titania: one of the two 512x512 authored combat maps.
    internal const float SpawnRadius = 110f;

    // Provisional first matchup. Keep this fixed while proving that fresh-start learning works;
    // changing these two constants is deliberately all that is required to try another pairing.
    internal const ConfigData.ShipTypes BeeShipType = ConfigData.ShipTypes.Wasp;
    internal const ConfigData.ShipTypes HumanShipType = ConfigData.ShipTypes.Frigate;

    internal static bool IsDedicatedTrainingRuntime =>
        ShouldApply(SceneManager.GetActiveScene().name);

    internal static bool ShouldApply(string sceneName)
    {
        return sceneName == TrainingSceneName;
    }

    internal static bool IsActiveFor(Stage stage)
    {
        return stage != null && stage.IsTrainingNueralNetwork && IsDedicatedTrainingRuntime;
    }

    internal static ConfigData.ShipTypes GetShipTypeForSide(int side)
    {
        if (side == ConfigData.Configuration.BeeSide)
        {
            return BeeShipType;
        }
        if (side == ConfigData.Configuration.HumanSide)
        {
            return HumanShipType;
        }
        throw new System.ArgumentOutOfRangeException(nameof(side), side, "RL training side must be Bees or Humans.");
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyToLoadedTrainingScene()
    {
        if (!IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = Object.FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            Debug.LogError("RL 1v1 training scene loaded without a Stage.");
            return;
        }

        Apply(stage);
    }

    internal static void Apply(Stage stage)
    {
        if (stage == null)
        {
            return;
        }

        // Training skips normal GameMenus.Setup(), while Level.SetupLevel() still expects the
        // serialized ActionBox hierarchy to exist. Preserve it but keep it inactive.
        if (stage.Menus == null && stage.UIManager != null)
        {
            stage.Menus = stage.UIManager.GetComponentInChildren<GameMenus>(true);
        }
        if (stage.Menus != null && stage.Menus.ActionBox == null && stage.Menus.SquadActionBoxUI != null)
        {
            stage.Menus.ActionBox = stage.Menus.SquadActionBoxUI.GetComponent<SquadActionBox>();
        }
        PreserveLegacySetupUi(stage);

        stage.IsTrainingHiveMind = false;
        stage.IsTrainingNueralNetwork = true;
        stage.ActivateHiveMind = false;
        stage.ActivateBrains = false; // The new RL controller will own actions; the historical Brain path is dormant.
        stage.DoesUserHaveController = false;
        stage.UseFullyRandomSquads = true;
        stage.UseFullyRandomEnemySquads = false;
        stage.HasRandomizedOptions = false;
        stage.IsRendering = true;
        stage.LevelCount = TrainingLevelCount;
        stage.GeneratedSquadCountOverride = 1;
        stage.OverrideMapIndex = TrainingMapIndex;
        stage.TimeoutTime = TrainingTimeoutSeconds;
        stage.InitialCommandDelay = 0;

        stage.OverrideBeeShipTypes = new List<ConfigData.ShipTypes> { BeeShipType };
        stage.OverrideHumanShipTypes = new List<ConfigData.ShipTypes> { HumanShipType };
    }

    private static void PreserveLegacySetupUi(Stage stage)
    {
        if (stage.UIManager == null)
        {
            return;
        }

        Transform uiManagerTransform = stage.UIManager.transform;
        if (stage.UIElements != null)
        {
            for (int i = stage.UIElements.Count - 1; i >= 0; i--)
            {
                GameObject uiElement = stage.UIElements[i];
                if (uiElement == null)
                {
                    continue;
                }

                Transform elementTransform = uiElement.transform;
                if (elementTransform == uiManagerTransform ||
                    elementTransform.IsChildOf(uiManagerTransform) ||
                    uiManagerTransform.IsChildOf(elementTransform))
                {
                    uiElement.SetActive(false);
                    stage.UIElements.RemoveAt(i);
                }
            }
        }

        stage.UIManager.SetActive(false);
    }
}
