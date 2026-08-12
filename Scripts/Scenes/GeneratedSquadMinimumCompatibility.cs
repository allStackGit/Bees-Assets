using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Compatibility guard for Stage's legacy generated-squad range formula. Stage currently evaluates
/// RandomInt(max - minimum) + 1 + minimum, which makes every positive configured minimum exclusive.
/// Scene-loaded runs before Start/finalization, so temporarily reducing the serialized minimum by one
/// restores the intended inclusive [minimum,max] range; the authored value is restored after setup.
/// </summary>
internal static class GeneratedSquadMinimumCompatibility
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
    {
        Stage stage = Object.FindFirstObjectByType<Stage>();
        if (stage == null || stage.GeneratedSquadCountMinimum <= 0)
        {
            return;
        }

        int authoredMinimum = stage.GeneratedSquadCountMinimum;
        stage.GeneratedSquadCountMinimum = authoredMinimum - 1;
        stage.StartCoroutine(RestoreAfterFinalization(stage, authoredMinimum));
    }

    private static IEnumerator RestoreAfterFinalization(Stage stage, int authoredMinimum)
    {
        while (stage != null && !stage.IsFinalized)
        {
            yield return null;
        }
        if (stage != null)
        {
            stage.GeneratedSquadCountMinimum = authoredMinimum;
        }
    }
}
