using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SocketOpenLevelLifecycleTests
    {
        [Test]
        public void ClosingLevelAlwaysRetiresReconnectAndPendingSetupOwnership()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));

            Assert.That(source, Does.Not.Contain("if (IsLevelSetupOnServer)"),
                "A level may close before its initial setup response arrives.");
            Assert.That(source, Does.Contain("ConfigData.Socket.StandingRequests.RemoveWhere"));
            Assert.That(source, Does.Contain("request is SetupLevelRequest"));
            Assert.That(source, Does.Contain("request is ReconnectLevelRequest"));
            Assert.That(source, Does.Contain("ConfigData.Socket.OpenLevels.Remove(this)"));
            Assert.That(source, Does.Contain("IsLevelConnectedToServer = false"));
        }
    }
}
