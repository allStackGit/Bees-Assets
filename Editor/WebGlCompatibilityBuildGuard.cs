using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Keeps Bees Web builds on the WebAssembly MVP call model for now.
/// Unity 6000.5 has a known Web Player failure that can surface as
/// "RuntimeError: index out of bounds" when WebAssembly 2023 is enabled,
/// and this project also carries a NativeWebSocket JavaScript bridge whose
/// callback compatibility depends on the selected WebAssembly call-table mode.
/// </summary>
[InitializeOnLoad]
public sealed class WebGlCompatibilityBuildGuard : IPreprocessBuildWithReport
{
    static WebGlCompatibilityBuildGuard()
    {
        EditorApplication.delayCall += ApplyCompatibilitySettings;
    }

    public int callbackOrder => int.MinValue;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform == BuildTarget.WebGL)
        {
            ApplyCompatibilitySettings();
        }
    }

    private static void ApplyCompatibilitySettings()
    {
        bool changed = false;

        if (PlayerSettings.WebGL.wasm2023)
        {
            PlayerSettings.WebGL.wasm2023 = false;
            changed = true;
        }

        if (PlayerSettings.WebGL.webAssemblyTable)
        {
            PlayerSettings.WebGL.webAssemblyTable = false;
            changed = true;
        }

        if (changed)
        {
            Debug.LogWarning(
                "Bees WebGL compatibility: disabled WebAssembly 2023/WebAssembly.Table " +
                "to avoid the Unity 6000.5 Web Player call-table crash.");
        }
    }
}
