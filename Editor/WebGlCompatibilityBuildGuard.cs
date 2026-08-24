using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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

/// <summary>
/// Owns the outermost responsive boundary for WebGL: browser viewport -> Unity canvas.
/// Unity RectTransform/CanvasScaler repairs cannot consume browser whitespace when the
/// generated HTML keeps #unity-canvas inside a fixed-aspect desktop rectangle, because
/// Screen.width/Screen.height then report only that already-letterboxed canvas.
///
/// Patch the generated player shell after every WebGL build so the browser canvas fills
/// the live viewport and Unity keeps its drawing buffer matched to the CSS canvas size.
/// This intentionally runs at the end of post-processing so earlier template/build steps
/// cannot silently restore the authored desktop dimensions afterward.
/// </summary>
public sealed class WebGlResponsiveViewportBuildGuard : IPostprocessBuildWithReport
{
    internal const string ViewportMarker = "BEES_FULLSCREEN_VIEWPORT_BEGIN";

    private const string ViewportStyle = @"
<!-- BEES_FULLSCREEN_VIEWPORT_BEGIN -->
<style id=""bees-fullscreen-webgl"">
html,
body {
    width: 100% !important;
    height: 100% !important;
    margin: 0 !important;
    padding: 0 !important;
    overflow: hidden !important;
}

#unity-container,
#unity-container.unity-desktop,
#unity-container.unity-mobile {
    position: fixed !important;
    left: 0 !important;
    top: 0 !important;
    right: 0 !important;
    bottom: 0 !important;
    width: 100vw !important;
    height: 100vh !important;
    min-width: 0 !important;
    min-height: 0 !important;
    max-width: none !important;
    max-height: none !important;
    aspect-ratio: auto !important;
    transform: none !important;
}

#unity-canvas,
#unity-container.unity-desktop #unity-canvas,
#unity-container.unity-mobile #unity-canvas {
    position: absolute !important;
    inset: 0 !important;
    display: block !important;
    width: 100% !important;
    height: 100% !important;
    min-width: 0 !important;
    min-height: 0 !important;
    max-width: none !important;
    max-height: none !important;
    aspect-ratio: auto !important;
    object-fit: fill !important;
}
</style>
<!-- BEES_FULLSCREEN_VIEWPORT_END -->
";

    private static readonly Regex CreateUnityInstanceCall = new Regex(
        @"(?m)^(?<indent>[ \t]*)createUnityInstance\s*\(\s*canvas\s*,\s*config",
        RegexOptions.CultureInvariant);

    public int callbackOrder => int.MaxValue;

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.WebGL)
        {
            return;
        }

        string indexPath = ResolveIndexPath(report.summary.outputPath);
        if (!File.Exists(indexPath))
        {
            throw new BuildFailedException(
                $"Bees WebGL responsive viewport guard could not find generated index.html at '{indexPath}'. " +
                "The build cannot be considered responsive until the browser canvas shell is patched.");
        }

        try
        {
            string html = File.ReadAllText(indexPath);
            string patched = PatchWebGlIndex(html);
            if (!string.Equals(html, patched, StringComparison.Ordinal))
            {
                File.WriteAllText(indexPath, patched, new UTF8Encoding(false));
            }

            Debug.Log($"Bees WebGL responsive viewport: patched {indexPath}");
        }
        catch (BuildFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new BuildFailedException(
                $"Bees WebGL responsive viewport patch failed for '{indexPath}': {exception.Message}");
        }
    }

    internal static string ResolveIndexPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return string.Empty;
        }

        if (string.Equals(Path.GetExtension(outputPath), ".html", StringComparison.OrdinalIgnoreCase))
        {
            return outputPath;
        }

        return Path.Combine(outputPath, "index.html");
    }

    internal static string PatchWebGlIndex(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidDataException("Generated WebGL index.html was empty.");
        }

        string patched = html;
        if (patched.IndexOf(ViewportMarker, StringComparison.Ordinal) < 0)
        {
            int headEnd = patched.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headEnd < 0)
            {
                throw new InvalidDataException(
                    "Generated WebGL index.html has no </head> element for the fullscreen viewport override.");
            }

            patched = patched.Insert(headEnd, ViewportStyle + Environment.NewLine);
        }

        if (!Regex.IsMatch(
                patched,
                @"config\.matchWebGLToCanvasSize\s*=\s*true\s*;",
                RegexOptions.CultureInvariant))
        {
            Match createCall = CreateUnityInstanceCall.Match(patched);
            if (!createCall.Success)
            {
                throw new InvalidDataException(
                    "Generated WebGL index.html has no createUnityInstance(canvas, config, ...) call. " +
                    "The template changed and the drawing-buffer resize contract must be reviewed.");
            }

            string assignment =
                createCall.Groups["indent"].Value +
                "config.matchWebGLToCanvasSize = true; // BEES_FULLSCREEN_VIEWPORT\n";
            patched = patched.Insert(createCall.Index, assignment);
        }

        return patched;
    }
}
