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
        public void DedicatedRlTrainingStopsManagingSocketOnlyAfterStartupIsComplete()
        {
            string sceneSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "Scene.cs"));
            string levelSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.cs"));

            int policyStart = sceneSource.IndexOf("private bool CanRunWithoutServer()", StringComparison.Ordinal);
            int updateStart = sceneSource.IndexOf("protected virtual void Update()", policyStart, StringComparison.Ordinal);

            Assert.That(policyStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(updateStart, Is.GreaterThan(policyStart));

            string policy = sceneSource.Substring(policyStart, updateStart - policyStart);
            Assert.That(policy, Does.Contain("RlOneVsOneTrainingBootstrap.IsActiveFor(stage)"));
            Assert.That(policy, Does.Contain("IsFinalized"));
            Assert.That(policy, Does.Contain("ConfigData.AreAllSettingsLoaded"));
            Assert.That(policy, Does.Contain("ConfigData.IsAllUserDataLoaded"));

            Assert.That(sceneSource, Does.Contain("if (!canRunWithoutServer)"),
                "Normal socket polling/reconnect/disconnect UI must be bypassed only after dedicated RL startup is complete.");
            Assert.That(levelSource, Does.Contain("if (!global::RlOneVsOneTrainingBootstrap.IsActiveFor(Stage))"));
            Assert.That(levelSource, Does.Contain("LevelConstructor.RequestServerSetup();"),
                "Ordinary levels must retain their server setup request while the dedicated RL level skips it.");
        }
    }
}
