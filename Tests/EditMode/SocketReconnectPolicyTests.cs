using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SocketReconnectPolicyTests
    {
        [Test]
        public void SocketManagerRetriesAnyUnopenedConnectionWithoutShowingEarlyDisconnectUi()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "Scene.cs"));

            int retryUpdate = source.IndexOf(
                "if (IsSocketManager && !ConfigData.Socket.IsOpen && !ConfigData.Socket.KeepClosed)",
                StringComparison.Ordinal);
            int closedUi = source.IndexOf(
                "if (ConfigData.Socket.HasClosed && IsSocketManager)",
                StringComparison.Ordinal);

            Assert.That(retryUpdate, Is.GreaterThanOrEqualTo(0),
                "The automatic retry timer must run for an initial connection that never opens, even if OnClose was not raised.");
            Assert.That(closedUi, Is.GreaterThan(retryUpdate),
                "Disconnect UI should remain gated by HasClosed rather than appearing during the normal initial connection window.");
        }

        [Test]
        public void RetryCallbackDoesNotRequireHasClosed()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "Scene.cs"));
            int methodStart = source.IndexOf("private void AutomaticConnectionRetry()", StringComparison.Ordinal);
            int methodEnd = source.IndexOf("private bool AreOpenLevelsReconnected()", methodStart, StringComparison.Ordinal);

            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, methodEnd - methodStart);
            Assert.That(method, Does.Contain("ConfigData.Socket.IsOpen"));
            Assert.That(method, Does.Contain("ConfigData.Socket.KeepClosed"));
            Assert.That(method, Does.Not.Contain("ConfigData.Socket.HasClosed"));
            Assert.That(method, Does.Contain("ConfigData.RetryConnection();"));
        }

        [Test]
        public void DedicatedTrainingSocketShutdownIsPermanentAndNonError()
        {
            Type socketType = RuntimeAssembly.GetType("Assets.Scripts.Server.Socket");
            object socket = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Server.Socket");

            RuntimeAssembly.SetField(socket, "IsOpen", true);
            RuntimeAssembly.SetField(socket, "HasClosed", true);
            RuntimeAssembly.SetField(socket, "KeepClosed", false);

            Assert.DoesNotThrow(() =>
                RuntimeAssembly.Invoke(socket, "DisableForDedicatedTraining"));

            Assert.That(RuntimeAssembly.GetField(socket, "KeepClosed"), Is.True);
            Assert.That(RuntimeAssembly.GetField(socket, "IsOpen"), Is.False);
            Assert.That(RuntimeAssembly.GetField(socket, "HasClosed"), Is.False);

            Assert.DoesNotThrow(() =>
                RuntimeAssembly.Invoke(socket, "DisableForDedicatedTraining"));
            Assert.That(RuntimeAssembly.GetField(socket, "KeepClosed"), Is.True);
        }
    }
}
