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

        stage.IsTrainingHiveMind = true;
        stage.IsTrainingNueralNetwork = false;
        stage.ActivateHiveMind = true;
        stage.DoesUserHaveController = false;
        stage.UseFullyRandomSquads = true;
        stage.IsRendering = false;
        stage.LevelCount = TrainingLevelCount;
        stage.TimeoutTime = TrainingTimeoutSeconds;
        stage.InitialCommandDelay = TrainingInitialCommandDelaySeconds;
    }
}
