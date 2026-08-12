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
        public void FailedBasicWritesRemainRetryableAndSuccessCoversBothWireConventions()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SocketResponseLifecycleGuard.cs"));

            Assert.That(source, Does.Contain("response.RequestType == ConfigData.RequestTypes.StoreCommands"));
            Assert.That(source, Does.Contain("response.RequestType == ConfigData.RequestTypes.StoreUserData"));
            Assert.That(source, Does.Contain("response.RequestType == ConfigData.RequestTypes.SendRLData"));
            Assert.That(source, Does.Contain("status == 1"),
                "Legacy success acknowledgements must remain accepted.");
            Assert.That(source, Does.Contain("status >= 200 && status < 300"),
                "Current BeesServer HTTP-style success acknowledgements must be accepted.");
            Assert.That(source, Does.Contain("IsSuccessfulWriteStatus(response.Status)"));
            Assert.That(source, Does.Contain("response.Status == 409"));
            Assert.That(source, Does.Contain("response.Status == 403"));
            Assert.That(source, Does.Contain("socket.GetStandingRequest(response.Hash)"));
            Assert.That(source, Does.Contain("keeping it pending for retry"),
                "A retryable failed write acknowledgement must leave its request standing so the normal resend policy can retry it.");
        }

        [Test]
        public void FailedTypedPayloadResponsesAreConsumedBeforeSuccessDispatch()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SocketResponseLifecycleGuard.cs"));

            Assert.That(source, Does.Contain("ConfigData.RequestTypes.SetupLevel"));
            Assert.That(source, Does.Contain("ConfigData.RequestTypes.ReconnectLevel"));
            Assert.That(source, Does.Contain("ConfigData.RequestTypes.GetMatchupStrategy"));
            Assert.That(source, Does.Contain("ConfigData.RequestTypes.GetStrategy"));
            Assert.That(source, Does.Contain("IsTypedPayloadResponse(response.RequestType) && response.Status >= 400"));
            Assert.That(source, Does.Contain("return true;"),
                "Failed typed responses must be consumed before Socket.Message can claim their hash or parse success-only fields.");
            Assert.That(source, Does.Contain("socket.StandingRequests.Remove(standingRequest)"),
                "Terminal authorization failures should retire the standing request without applying typed success state.");
            Assert.That(source, Does.Contain("keeping it pending for retry without dispatching the incomplete payload"),
                "Retryable typed failures must remain available for reconnect/resend recovery.");
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
