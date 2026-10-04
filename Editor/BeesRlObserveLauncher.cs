using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BeesRlObserveLauncher
{
    private const string TrainingScenePath = "Assets/Scenes/RL 1v1 Training.unity";

    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("RL observation requested while the Editor is already entering or in Play mode.");
            return;
        }

        EditorSceneManager.OpenScene(TrainingScenePath, OpenSceneMode.Single);
        Debug.Log("RL visual observation scene loaded; entering Play mode.");
        EditorApplication.delayCall += EnterPlayMode;
    }

    private static void EnterPlayMode()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += EnterPlayMode;
            return;
        }

        EditorApplication.isPlaying = true;
    }
}
