using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class WebGlResponsiveViewportTemplateTests
    {
        private const string GuardTypeName = "WebGlResponsiveViewportBuildGuard";

        private static Type GetGuardType()
        {
            Assembly editorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp-Editor");
            Assert.That(editorAssembly, Is.Not.Null);

            Type guardType = editorAssembly.GetType(GuardTypeName);
            Assert.That(guardType, Is.Not.Null);
            return guardType;
        }

        private static string ReadTemplate()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath,
                "WebGLTemplates",
                "BeesResponsive",
                "index.html"));
        }

        [Test]
        public void WebGlBuildAlwaysSelectsTrackedResponsiveTemplate()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Editor",
                "WebGlCompatibilityBuildGuard.cs"));

            Assert.That(source, Does.Contain("ResponsiveTemplate = \"PROJECT:BeesResponsive\""));
            Assert.That(source, Does.Contain("PlayerSettings.WebGL.template = ResponsiveTemplate"),
                "A developer-local built-in template selection must not be able to restore the 16:9 browser shell.");
        }

        [Test]
        public void TrackedTemplateOwnsEntireBrowserViewport()
        {
            string template = ReadTemplate();

            Assert.That(template, Does.Contain("BEES_FULLSCREEN_VIEWPORT_BEGIN"));
            Assert.That(template, Does.Contain("BEES_FULLSCREEN_HOST_BEGIN"));
            Assert.That(template, Does.Contain("position: fixed !important;"));
            Assert.That(template, Does.Contain("width: 100vw !important;"));
            Assert.That(template, Does.Contain("height: 100vh !important;"));
            Assert.That(template, Does.Contain("matchWebGLToCanvasSize: true"));
            Assert.That(template, Does.Contain("window.frameElement"),
                "A same-origin host iframe must not remain as an outer 16:9 boundary around a fullscreen inner canvas.");
            Assert.That(template, Does.Not.Contain("aspect-ratio: 16"));
        }

        [Test]
        public void ActualTrackedTemplateAlreadySatisfiesPostBuildViewportVerification()
        {
            Type guardType = GetGuardType();
            MethodInfo patchMethod = guardType.GetMethod(
                "PatchWebGlIndex",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo validateMethod = guardType.GetMethod(
                "ValidatePatchedWebGlIndex",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(patchMethod, Is.Not.Null);
            Assert.That(validateMethod, Is.Not.Null);

            string template = ReadTemplate();
            string patched = (string)patchMethod.Invoke(null, new object[] { template });

            Assert.That(patched, Is.EqualTo(template),
                "The tracked template itself should satisfy the contract; post-processing is only a fallback/verification layer.");
            Assert.DoesNotThrow(() => validateMethod.Invoke(null, new object[] { patched }));
        }

        [Test]
        public void FallbackPatchAlsoEscapesSameOriginAspectRatioHost()
        {
            const string generatedIndex = @"<!doctype html>
<html>
<head></head>
<body>
<div id=""unity-container"" style=""aspect-ratio:16/9""><canvas id=""unity-canvas""></canvas></div>
<script>
var canvas = document.querySelector(""#unity-canvas"");
var config = { matchWebGLToCanvasSize: false };
createUnityInstance(canvas, config, function () {});
</script>
</body>
</html>";

            Type guardType = GetGuardType();
            MethodInfo patchMethod = guardType.GetMethod(
                "PatchWebGlIndex",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(patchMethod, Is.Not.Null);

            string patched = (string)patchMethod.Invoke(null, new object[] { generatedIndex });

            Assert.That(patched, Does.Contain("BEES_FULLSCREEN_HOST_BEGIN"));
            Assert.That(patched, Does.Contain("window.frameElement"));
            Assert.That(patched, Does.Contain("config.matchWebGLToCanvasSize = true;"));
        }
    }
}
