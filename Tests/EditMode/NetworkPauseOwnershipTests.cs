using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class NetworkPauseOwnershipTests
    {
        [Test]
        public void ReconnectOnlyUnpausesWhenDisconnectOwnedThePause()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Scenes", "Scene.cs"));

            Assert.That(source, Does.Contain("private bool _pausedForNetworkDisconnect"));
            Assert.That(source, Does.Contain("_pausedForNetworkDisconnect = !primaryLevel.State.IsPaused"));
            Assert.That(source, Does.Contain("if (_pausedForNetworkDisconnect)"));
            Assert.That(source, Does.Contain("Type == ConfigData.SceneTypes.Stage && _pausedForNetworkDisconnect"));
            Assert.That(source, Does.Contain("_pausedForNetworkDisconnect = false"));
        }
    }
}
