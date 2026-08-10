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
        public void ClosingLevelRemovesItFromReconnectSet()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Campaign.Shared.cs"));

            Assert.That(source, Does.Contain("if (IsLevelSetupOnServer)"));
            Assert.That(source, Does.Contain("ConfigData.Socket.OpenLevels.Remove(this)"));
            Assert.That(source, Does.Contain("IsLevelConnectedToServer = false"));
        }
    }
}
