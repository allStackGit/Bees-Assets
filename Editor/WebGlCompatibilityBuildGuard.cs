using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Keeps Bees Web builds on the WebAssembly MVP call model for now and selects the
/// project-owned responsive WebGL template. The template choice is a build invariant:
/// browser viewport ownership must not depend on whichever built-in Unity template was
/// last selected in a developer's local Project Settings.
/// </summary>
[InitializeOnLoad]
public sealed class WebGlCompatibilityBuildGuard : IPreprocessBuildWithReport
{
    internal const string ResponsiveTemplate = "PROJECT:BeesResponsive";

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

        if (!string.Equals(
                PlayerSettings.WebGL.template,
                ResponsiveTemplate,
                StringComparison.Ordinal))
        {
            PlayerSettings.WebGL.template = ResponsiveTemplate;
            changed = true;
        }

        if (changed)
        {
            Debug.LogWarning(
                "Bees WebGL compatibility: enforced WebAssembly MVP call-table settings and " +
                $"the '{ResponsiveTemplate}' fullscreen viewport template.");
        }
    }
}

/// <summary>
/// Owns the outermost responsive boundary for WebGL: browser viewport -> Unity canvas.
/// Unity RectTransform/CanvasScaler repairs cannot consume browser whitespace when the
/// generated HTML keeps #unity-canvas inside a fixed-aspect desktop rectangle, because
/// Screen.width/Screen.height then report only that already-letterboxed canvas.
///
/// Bees now generates WebGL through the tracked BeesResponsive template. This post-build
/// guard remains intentionally redundant: it verifies/repairs generated output so a template
/// regression or changed Unity generator cannot silently restore a 16:9 browser shell.
/// </summary>
public sealed class WebGlResponsiveViewportBuildGuard : IPostprocessBuildWithReport
{
    internal const string ViewportMarker = "BEES_FULLSCREEN_VIEWPORT_BEGIN";
    internal const string HostViewportMarker = "BEES_FULLSCREEN_HOST_BEGIN";

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
    inset: 0 !important;
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

    // Dedicated deployments sometimes wrap index.html in a same-origin iframe. CSS inside
    // the Unity document cannot resize that outer frame, so make the host frame consume the
    // parent viewport as well. Cross-origin embedding is left untouched because frameElement
    // access is unavailable there.
    private const string HostViewportScript = @"
<!-- BEES_FULLSCREEN_HOST_BEGIN -->
<script id=""bees-fullscreen-host"">
(function () {
    function setImportant(style, property, value) {
        style.setProperty(property, value, 'important');
    }

    function fillViewport() {
        var root = document.documentElement;
        var body = document.body;
        var container = document.getElementById('unity-container');
        var canvas = document.getElementById('unity-canvas');

        if (root) {
            setImportant(root.style, 'width', '100%');
            setImportant(root.style, 'height', '100%');
            setImportant(root.style, 'margin', '0');
            setImportant(root.style, 'overflow', 'hidden');
        }
        if (body) {
            setImportant(body.style, 'width', '100%');
            setImportant(body.style, 'height', '100%');
            setImportant(body.style, 'margin', '0');
            setImportant(body.style, 'overflow', 'hidden');
        }
        if (container) {
            setImportant(container.style, 'position', 'fixed');
            setImportant(container.style, 'inset', '0');
            setImportant(container.style, 'width', '100vw');
            setImportant(container.style, 'height', '100vh');
            setImportant(container.style, 'max-width', 'none');
            setImportant(container.style, 'max-height', 'none');
            setImportant(container.style, 'aspect-ratio', 'auto');
            setImportant(container.style, 'transform', 'none');
        }
        if (canvas) {
            setImportant(canvas.style, 'position', 'absolute');
            setImportant(canvas.style, 'inset', '0');
            setImportant(canvas.style, 'width', '100%');
            setImportant(canvas.style, 'height', '100%');
            setImportant(canvas.style, 'max-width', 'none');
            setImportant(canvas.style, 'max-height', 'none');
            setImportant(canvas.style, 'aspect-ratio', 'auto');
            setImportant(canvas.style, 'object-fit', 'fill');
        }

        try {
            var frame = window.frameElement;
            if (frame) {
                setImportant(frame.style, 'position', 'fixed');
                setImportant(frame.style, 'inset', '0');
                setImportant(frame.style, 'width', '100vw');
                setImportant(frame.style, 'height', '100vh');
                setImportant(frame.style, 'max-width', 'none');
                setImportant(frame.style, 'max-height', 'none');
                setImportant(frame.style, 'aspect-ratio', 'auto');
                setImportant(frame.style, 'transform', 'none');

                var hostDocument = frame.ownerDocument;
                if (hostDocument && hostDocument.documentElement) {
                    setImportant(hostDocument.documentElement.style, 'width', '100%');
                    setImportant(hostDocument.documentElement.style, 'height', '100%');
                    setImportant(hostDocument.documentElement.style, 'margin', '0');
                    setImportant(hostDocument.documentElement.style, 'overflow', 'hidden');
                }
                if (hostDocument && hostDocument.body) {
                    setImportant(hostDocument.body.style, 'width', '100%');
                    setImportant(hostDocument.body.style, 'height', '100%');
                    setImportant(hostDocument.body.style, 'margin', '0');
                    setImportant(hostDocument.body.style, 'overflow', 'hidden');
                }
            }
        } catch (_) {
            // Cross-origin parent: the embedding page must own its iframe dimensions.
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', fillViewport, { once: true });
    } else {
        fillViewport();
    }
    window.addEventListener('resize', fillViewport);
})();
</script>
<!-- BEES_FULLSCREEN_HOST_END -->
";

    private static readonly Regex CreateUnityInstanceCall = new Regex(
        @"(?m)^(?<indent>[ \t]*)createUnityInstance\s*\(\s*canvas\s*,\s*config",
        RegexOptions.CultureInvariant);

    private static readonly Regex MatchWebGlToCanvasSizeEnabled = new Regex(
        @"(?:config\s*\.\s*matchWebGLToCanvasSize\s*=\s*true\s*;|matchWebGLToCanvasSize\s*:\s*true\s*,?)",
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
                "The build cannot be considered responsive until the browser canvas shell is verified.");
        }

        try
        {
            string html = File.ReadAllText(indexPath);
            string patched = PatchWebGlIndex(html);
            ValidatePatchedWebGlIndex(patched);
            if (!string.Equals(html, patched, StringComparison.Ordinal))
            {
                File.WriteAllText(indexPath, patched, new UTF8Encoding(false));
            }

            Debug.Log($"Bees WebGL responsive viewport: verified {indexPath}");
        }
        catch (BuildFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new BuildFailedException(
                $"Bees WebGL responsive viewport verification failed for '{indexPath}': {exception.Message}");
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
        int headEnd = patched.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0)
        {
            throw new InvalidDataException(
                "Generated WebGL index.html has no </head> element for the fullscreen viewport contract.");
        }

        if (patched.IndexOf(ViewportMarker, StringComparison.Ordinal) < 0)
        {
            patched = patched.Insert(headEnd, ViewportStyle + Environment.NewLine);
            headEnd = patched.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        }

        if (patched.IndexOf(HostViewportMarker, StringComparison.Ordinal) < 0)
        {
            patched = patched.Insert(headEnd, HostViewportScript + Environment.NewLine);
        }

        if (!MatchWebGlToCanvasSizeEnabled.IsMatch(patched))
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

    internal static void ValidatePatchedWebGlIndex(string html)
    {
        if (string.IsNullOrWhiteSpace(html) ||
            html.IndexOf(ViewportMarker, StringComparison.Ordinal) < 0 ||
            html.IndexOf(HostViewportMarker, StringComparison.Ordinal) < 0 ||
            !MatchWebGlToCanvasSizeEnabled.IsMatch(html))
        {
            throw new InvalidDataException(
                "Generated WebGL player shell does not satisfy the Bees fullscreen viewport contract.");
        }
    }
}
