using System.IO;
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
    }
}
