using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class WebGlViewportRuntimeGuardTests
    {
        [Test]
        public void RuntimeBundleCanRepairViewportWithoutReplacingHostIndex()
        {
            string plugin = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Plugins",
                "WebGlViewport.jslib"));
            string runtimeGuard = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "WebGlViewportRuntimeGuard.cs"));
            string pluginMeta = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Plugins",
                "WebGlViewport.jslib.meta"));

            Assert.That(plugin, Does.Contain("BeesInstallResponsiveViewport"));
            Assert.That(plugin, Does.Contain("Module.canvas"),
                "The repair must target the actual Unity canvas rather than depend on a particular host-page selector.");
            Assert.That(plugin, Does.Contain("window.frameElement"),
                "Same-origin outer frames must not preserve a 16:9 boundary around the Unity document.");
            Assert.That(plugin, Does.Contain("Module.setCanvasSize"),
                "Legacy deployed host pages with matchWebGLToCanvasSize=false still need their render buffer resized.");
            Assert.That(plugin, Does.Contain("addEventListener('resize', fillViewport)"));
            Assert.That(plugin, Does.Not.Contain("aspect-ratio', '16"));

            Assert.That(runtimeGuard, Does.Contain("DllImport(\"__Internal\")"));
            Assert.That(runtimeGuard, Does.Contain("RuntimeInitializeLoadType.BeforeSceneLoad"));
            Assert.That(runtimeGuard, Does.Contain("BeesInstallResponsiveViewport();"),
                "The jslib repair must be invoked automatically by the WebGL player, not require a scene-specific component.");

            Assert.That(pluginMeta, Does.Contain("WebGL: WebGL"));
            Assert.That(pluginMeta, Does.Contain("enabled: 1"));
        }
    }
}
