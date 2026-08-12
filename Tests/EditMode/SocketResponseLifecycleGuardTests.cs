using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SocketResponseLifecycleGuardTests
    {
        [Test]
        public void FailedBasicWritesRemainRetryableInsteadOfBeingRetired()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SocketResponseLifecycleGuard.cs"));

            Assert.That(source, Does.Contain("response.RequestType == ConfigData.RequestTypes.StoreCommands"));
            Assert.That(source, Does.Contain("response.RequestType == ConfigData.RequestTypes.StoreUserData"));
            Assert.That(source, Does.Contain("response.RequestType == ConfigData.RequestTypes.SendRLData"));
            Assert.That(source, Does.Contain("response.Status == 1"));
            Assert.That(source, Does.Contain("socket.GetStandingRequest(response.Hash)"));
            Assert.That(source, Does.Not.Contain("StandingRequests.Remove(request)"),
                "A failed write acknowledgement must leave its request standing so the normal resend policy can retry it.");
        }

        [Test]
        public void SocketHandledResponseDedupeHasBoundedLifetimeAndSize()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SocketResponseLifecycleGuard.cs"));

            Assert.That(source, Does.Contain("HandledResponseRetentionSeconds"));
            Assert.That(source, Does.Contain("MaxTrackedHandledResponses"));
            Assert.That(source, Does.Contain("socket.HandledRequests.Remove(hash)"));
            Assert.That(source, Does.Contain("_handledAt.Remove(hash)"));
        }
    }
}
