using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class WebSocketWebGLCompatibilityTests
    {
        [Test]
        public void WebSocketCallbacksUseMakeDynCallInsteadOfLegacyModuleHelpers()
        {
            string pluginPath = Path.Combine(Application.dataPath, "Plugins", "WebSocket.jslib");
            string source = File.ReadAllText(pluginPath);

            StringAssert.DoesNotContain(
                "Module.dynCall_",
                source,
                "Unity 6 no longer guarantees the legacy Module.dynCall_* callback helpers.");
            StringAssert.Contains(
                "makeDynCall('vi', 'callback')",
                source,
                "The open callback must use the Unity/Emscripten makeDynCall bridge.");
            StringAssert.Contains(
                "makeDynCall('viii', 'callback')",
                source,
                "The message callback must use the Unity/Emscripten makeDynCall bridge.");
            StringAssert.Contains(
                "makeDynCall('vii', 'callback')",
                source,
                "The error and close callbacks must use the Unity/Emscripten makeDynCall bridge.");
        }
    }
}
