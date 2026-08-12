using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Compatibility guard for Stage's legacy generated-squad range formula. Stage currently evaluates
/// RandomInt(max - minimum) + 1 + minimum, which makes every positive configured minimum exclusive.
/// Normalize each Stage instance once and keep the corrected runtime minimum for its full lifetime,
/// because Level.ResetLevel() reuses the same Stage and reruns the same range formula.
/// </summary>
internal static class GeneratedSquadMinimumCompatibility
{
    private static readonly HashSet<Stage> AdjustedStages = new HashSet<Stage>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
    {
        Stage stage = Object.FindFirstObjectByType<Stage>();
        if (stage == null || stage.GeneratedSquadCountMinimum <= 0 || !AdjustedStages.Add(stage))
        {
            return;
        }

        stage.GeneratedSquadCountMinimum--;
    }
}
