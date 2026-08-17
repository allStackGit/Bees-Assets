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
    }
}
