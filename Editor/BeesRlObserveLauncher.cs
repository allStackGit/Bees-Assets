using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class BeesRlObserveLauncher
{
    private const string TrainingScenePath = "Assets/Scenes/RL 1v1 Training.unity";
    private const string ObserveStopFilePrefix = "--bees-rl-observe-stop-file=";

    // Domain reload occurs on entering Play mode. Register on every Editor domain load
    // so stopping Play mode can notify the operator even after that reload.
    static BeesRlObserveLauncher()
    {
        if (GetObserveStopFile() == null)
        {
            return;
        }
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.quitting += OnEditorQuitting;
    }

    private static string GetObserveStopFile()
    {
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument.StartsWith(ObserveStopFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string file = argument.Substring(ObserveStopFilePrefix.Length);
                return Path.IsPathRooted(file) ? file : null;
            }
        }
        return null;
    }

    private static void SignalObserverStopped()
    {
        string file = GetObserveStopFile();
        if (file == null || !Directory.Exists(Path.GetDirectoryName(file)))
        {
            return;
        }
        try
        {
            File.WriteAllText(file, "stopped");
        }
        catch (Exception error)
        {
            Debug.LogWarning("Unable to notify Bees observer that Unity stopped: " + error.Message);
        }
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            SignalObserverStopped();
        }
    }

    private static void OnEditorQuitting()
    {
        SignalObserverStopped();
    }

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
