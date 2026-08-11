using Assets.Scripts;
using Assets.Scripts.UIComponents;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Restores the intentional automated Hive Mind training configuration for the dedicated
/// training scene. The scene asset has drifted back toward an interactive single-level
/// configuration, while the historical authored training setup used 16 headless levels
/// with 420-second episode timeouts.
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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyToLoadedTrainingScene()
    {
        if (SceneManager.GetActiveScene().name != TrainingSceneName)
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
        // ActionBox setup path before the training UI objects are destroyed at frame end.
        // Bind only the serialized objects that path needs; do not initialize player menus.
        if (stage.Menus == null && stage.UIManager != null)
        {
            stage.Menus = stage.UIManager.GetComponentInChildren<GameMenus>(true);
        }
        if (stage.Menus != null && stage.Menus.ActionBox == null && stage.Menus.SquadActionBoxUI != null)
        {
            stage.Menus.ActionBox = stage.Menus.SquadActionBoxUI.GetComponent<SquadActionBox>();
        }

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
}
