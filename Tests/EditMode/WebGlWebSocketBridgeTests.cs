using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class WebGlWebSocketBridgeTests
    {
        private static string ReadBridge()
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, "Plugins", "WebSocket.jslib"));
        }

        private static string PatchWebGlIndex(string html)
        {
            Assembly editorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp-Editor");
            Assert.That(editorAssembly, Is.Not.Null,
                "The predefined Editor assembly must be loaded during EditMode tests.");

            Type guardType = editorAssembly.GetType("WebGlResponsiveViewportBuildGuard");
            Assert.That(guardType, Is.Not.Null,
                "The WebGL post-build viewport guard must remain in the Editor assembly.");

            MethodInfo patchMethod = guardType.GetMethod(
                "PatchWebGlIndex",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(patchMethod, Is.Not.Null);

            return (string)patchMethod.Invoke(null, new object[] { html });
        }

        [Test]
        public void ManagedCallbacksUseEmscriptenMakeDynCallInsteadOfLegacyModuleHelpers()
        {
            string source = ReadBridge();

            Assert.That(source, Does.Not.Contain("Module.dynCall_"),
                "Unity 6 WebGL must not depend on legacy Module.dynCall_* exports.");
            Assert.That(source, Does.Contain("makeDynCall('vi', 'callback')"),
                "The socket-open callback must use the one-int callback signature.");
            Assert.That(source, Does.Contain("makeDynCall('viii', 'callback')"),
                "The message callback must use the instance/pointer/length signature.");
            Assert.That(Regex.Matches(source, "makeDynCall\\('vii', 'callback'\\)").Count, Is.EqualTo(2),
                "Error and close callbacks must both use the two-int/pointer callback signature.");
        }

        [Test]
        public void MessageCallbackSlotIsInitializedUnderTheNameUsedByTheBridge()
        {
            string source = ReadBridge();

            Assert.That(source, Does.Contain("onMessage: null"));
            Assert.That(source, Does.Not.Contain("onMesssage"));
        }

        [Test]
        public void StartupServerSettingsParsingAvoidsDynamicDispatchOnIl2Cpp()
        {
            string[] settingsFiles =
            {
                "ShipStats.cs",
                "Configuration.cs",
                "StartingSettings.cs",
            };

            foreach (string file in settingsFiles)
            {
                string source = File.ReadAllText(Path.Combine(
                    Application.dataPath, "Scripts", "Settings", file));

                Assert.That(source, Does.Not.Contain("dynamic"),
                    $"{file} is part of WebGL startup and must not use C# dynamic dispatch under IL2CPP.");
                Assert.That(source, Does.Contain("JObject").Or.Contain("JArray"),
                    $"{file} should parse startup JSON through explicit Newtonsoft token types.");
            }
        }

        [Test]
        public void ProfileBootstrapParsingAvoidsDynamicDispatchOnIl2Cpp()
        {
            string[] dataFiles =
            {
                "DataFile.cs",
                "UserData.cs",
                "UserProgressData.cs",
                "FleetData.cs",
                "SavedSquadsData.cs",
                "LevelData.cs",
                "UserSettingsData.cs",
                "AotJson.cs",
            };

            foreach (string file in dataFiles)
            {
                string source = File.ReadAllText(Path.Combine(
                    Application.dataPath, "Scripts", "Data", file));

                Assert.That(source, Does.Not.Contain("dynamic"),
                    $"{file} participates in WebGL profile bootstrap and must not use C# dynamic dispatch under IL2CPP.");
            }

            string parser = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "AotJson.cs"));
            Assert.That(parser, Does.Contain("JObject").And.Contain("JArray").And.Contain("JToken"));
        }

        [Test]
        public void WebGlRequestSerializationAvoidsDynamicDispatchOnIl2Cpp()
        {
            string socket = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));
            string request = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "ServerRequest.cs"));

            Assert.That(socket, Does.Contain("public void Send(object content)"));
            Assert.That(socket, Does.Not.Contain("public void Send(dynamic content)"),
                "Every startup settings request passes through Socket.Send, so the serialization boundary must remain AOT-safe.");
            Assert.That(request, Does.Not.Contain("public dynamic Request"),
                "The base request payload placeholder must not reintroduce runtime-bound dispatch on WebGL.");
        }

        [Test]
        public void WebBuildDisablesWasm2023CallTableMode()
        {
            string guard = File.ReadAllText(Path.Combine(
                Application.dataPath, "Editor", "WebGlCompatibilityBuildGuard.cs"));

            Assert.That(guard, Does.Contain("BuildTarget.WebGL"),
                "The compatibility override must stay scoped to Web builds.");
            Assert.That(guard, Does.Contain("PlayerSettings.WebGL.wasm2023 = false"),
                "Unity 6000.5 Web builds must avoid the affected WebAssembly 2023 mode.");
            Assert.That(guard, Does.Contain("PlayerSettings.WebGL.webAssemblyTable = false"),
                "The WebAssembly function-table mode must remain disabled until the Unity/NativeWebSocket compatibility boundary is upgraded.");
        }

        [Test]
        public void WebGlGeneratedPlayerShellFillsBrowserViewportInsteadOfPreservingDesktopAspect()
        {
            const string generatedIndex = @"<!doctype html>
<html>
<head>
<style>
#unity-container.unity-desktop { left: 50%; top: 50%; transform: translate(-50%, -50%); aspect-ratio: 16 / 9; }
#unity-container.unity-desktop #unity-canvas { width: 960px; height: 540px; max-width: 100%; max-height: 100%; object-fit: contain; }
</style>
</head>
<body>
<div id=""unity-container"" class=""unity-desktop""><canvas id=""unity-canvas""></canvas></div>
<script>
var canvas = document.querySelector(""#unity-canvas"");
var config = { matchWebGLToCanvasSize: false };
createUnityInstance(canvas, config, (progress) => {});
</script>
</body>
</html>";

            string patched = PatchWebGlIndex(generatedIndex);

            Assert.That(patched, Does.Contain("BEES_FULLSCREEN_VIEWPORT_BEGIN"));
            Assert.That(patched, Does.Contain("position: fixed !important;"),
                "The Unity container must be owned by the browser viewport, not a centered desktop rectangle.");
            Assert.That(patched, Does.Contain("width: 100vw !important;"));
            Assert.That(patched, Does.Contain("height: 100vh !important;"));
            Assert.That(patched, Does.Contain("transform: none !important;"));
            Assert.That(patched, Does.Contain("aspect-ratio: auto !important;"));
            Assert.That(patched, Does.Contain("object-fit: fill !important;"),
                "A browser-level contain rule must not preserve a 16:9 render island inside the live canvas box.");
            Assert.That(patched, Does.Contain("config.matchWebGLToCanvasSize = true;"),
                "Unity must resize its drawing buffer to the live CSS canvas so Screen.width/height see the real viewport.");
            Assert.That(
                patched.IndexOf("config.matchWebGLToCanvasSize = true;", StringComparison.Ordinal),
                Is.LessThan(patched.IndexOf("createUnityInstance(canvas, config", StringComparison.Ordinal)));

            string patchedAgain = PatchWebGlIndex(patched);
            Assert.That(patchedAgain, Is.EqualTo(patched),
                "The post-build patch must be idempotent if another build/deployment step invokes it again.");
            Assert.That(
                Regex.Matches(patched, "BEES_FULLSCREEN_VIEWPORT_BEGIN").Count,
                Is.EqualTo(1));
        }

        [Test]
        public void WebGlRequestTrackingAvoidsHashTableComparerDispatchOnIl2Cpp()
        {
            string requestSet = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "StandingRequestSet.cs"));

            Assert.That(requestSet, Does.Not.Contain("Dictionary<"),
                "The development WebGL trace crashed in Dictionary.TryInsert while Socket.LogRequest tracked Configuration.");
            Assert.That(requestSet, Does.Not.Contain("HashSet<"),
                "Request tracking must not reintroduce the comparer path that already failed under IL2CPP/WebGL.");
            Assert.That(requestSet, Does.Contain("List<ServerRequest>"));
            Assert.That(requestSet, Does.Contain("request.Hash == hash"),
                "Transport hash must remain the request identity contract after removing hash tables.");
        }

        [Test]
        public void WebGlSocketFactoryRunsNormalSocketFieldInitialization()
        {
            string factory = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SecureSocketFactory.cs"));
            string socket = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));

            Assert.That(factory, Does.Not.Contain("GetUninitializedObject"),
                "Formatter-style allocation skips Socket field initializers and leaves request/update collections null.");
            Assert.That(factory, Does.Not.Contain("FormatterServices"));
            Assert.That(factory, Does.Contain("websocketUrl: websocketUrl"));
            Assert.That(factory, Does.Contain("secured: true"));
            Assert.That(socket, Does.Contain("internal Socket(int port, string hostname, bool useWebSocketSharp, string websocketUrl, bool secured)"));
            Assert.That(socket, Does.Contain("_waitableRequests = new ServerRequestSet()"));
            Assert.That(socket, Does.Contain("_waitableRequestSnapshot = new List<ServerRequest>()"));
        }
    }
}
