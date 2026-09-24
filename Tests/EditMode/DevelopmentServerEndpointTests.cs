using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class DevelopmentServerEndpointTests
    {
        [Test]
        public void DevelopmentClientUsesBeesServerDevelopmentPort()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "ConfigData.cs"));

            Assert.That(source, Does.Contain("public const int DevelopmentPort = 7146;"));
            Assert.That(source, Does.Contain("public const string EditorServerHostname = \"127.0.0.1\";"));
            Assert.That(source, Does.Contain("#if UNITY_EDITOR"));
            Assert.That(source, Does.Contain("_socket = new Socket(DevelopmentPort, DevelopmentServerHostname, UseWebSocketSharp);"));
        }
    }
}
