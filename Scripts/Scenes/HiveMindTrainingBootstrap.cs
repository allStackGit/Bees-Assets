using Assets.Scripts;
using Assets.Scripts.UIComponents;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Restores the intentional automated Hive Mind training configuration for the dedicated
/// training runtime. The same Unity scene is also used by the player-facing Fish Tank mode,
/// so scene name alone is not sufficient to decide that automated training was requested.
/// </summary>
internal static class HiveMindTrainingBootstrap
{
    internal const string TrainingSceneName = "Hivemind Training";
    internal const int TrainingLevelCount = 16;
    internal const int TrainingTimeoutSeconds = 420;
    internal const int TrainingInitialCommandDelaySeconds = 1;

    private static readonly ConfigData.ShipTypes[] TrainingBeeShipTypes =
    {
        ConfigData.ShipTypes.Beehive,
        ConfigData.ShipTypes.Bumblebee,
        ConfigData.ShipTypes.CarpenterBee,
        ConfigData.ShipTypes.Honeybee,
        ConfigData.ShipTypes.Hornet,
        ConfigData.ShipTypes.Leafcutter,
        ConfigData.ShipTypes.Queen,
        ConfigData.ShipTypes.Wasp,
        ConfigData.ShipTypes.YellowJacket,
    };

    private static readonly ConfigData.ShipTypes[] TrainingHumanShipTypes =
    {
        ConfigData.ShipTypes.Barge,
        ConfigData.ShipTypes.Carrier,
        ConfigData.ShipTypes.Cruiser,
        ConfigData.ShipTypes.Dreadnought,
        ConfigData.ShipTypes.Factory,
        ConfigData.ShipTypes.FireBarge,
        ConfigData.ShipTypes.Flagship,
        ConfigData.ShipTypes.Frigate,
        ConfigData.ShipTypes.Gunship,
        ConfigData.ShipTypes.Scout,
        ConfigData.ShipTypes.WarpGate,
    };

    internal static bool IsDedicatedTrainingRuntime =>
        ShouldApply(SceneManager.GetActiveScene().name, ConfigData.CurrentGameMode);

    internal static bool ShouldApply(string sceneName, ConfigData.GameModes gameMode)
    {
        return sceneName == TrainingSceneName && gameMode != ConfigData.GameModes.FishTank;
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
            Debug.LogError("Hive Mind training scene loaded without a Stage.");
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

        // Training skips GameMenus.Setup(), but Level.SetupLevel() still reaches the legacy
        // ActionBox setup path on every episode. Keep that serialized hierarchy alive across
        // resets, but deactivate it so headless training does not render or process player UI.
        if (stage.Menus == null && stage.UIManager != null)
        {
            stage.Menus = stage.UIManager.GetComponentInChildren<GameMenus>(true);
        }
        if (stage.Menus != null && stage.Menus.ActionBox == null && stage.Menus.SquadActionBoxUI != null)
        {
            stage.Menus.ActionBox = stage.Menus.SquadActionBoxUI.GetComponent<SquadActionBox>();
        }
        PreserveLegacySetupUi(stage);

        stage.IsTrainingHiveMind = true;
        stage.IsTrainingNueralNetwork = false;
        stage.ActivateHiveMind = true;
        stage.DoesUserHaveController = false;
        stage.UseFullyRandomSquads = true;
        stage.IsRendering = false;
        stage.LevelCount = TrainingLevelCount;
        stage.TimeoutTime = TrainingTimeoutSeconds;
        stage.InitialCommandDelay = TrainingInitialCommandDelaySeconds;

        // Random Hive Mind training must not depend on the current player's unlock progress.
        // Spawned-only children (Drone/Striker/Beacon) are exercised through their owners and
        // HumanTarget is a scripted objective, so random squads use the complete primary fleet.
        stage.OverrideBeeShipTypes = new List<ConfigData.ShipTypes>(TrainingBeeShipTypes);
        stage.OverrideHumanShipTypes = new List<ConfigData.ShipTypes>(TrainingHumanShipTypes);
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
