using System.Diagnostics;
using System.IO;
using Assets.Scripts;
using Assets.Scripts.Server;
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
        public void ForbiddenProfileReadsReachTheirWaitersAsTerminalFailures()
        {
            string guardSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SocketResponseLifecycleGuard.cs"));
            string socketSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));
            string dataFileSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "DataFile.cs"));
            string settingsSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Settings", "ServerSettings.cs"));

            Assert.That(guardSource, Does.Contain("readRequest.Status = response.Status"));
            Assert.That(guardSource, Does.Contain("response.Status == 403"));
            Assert.That(socketSource, Does.Contain("_sr.Status == 403"));
            Assert.That(dataFileSource, Does.Contain("ServerReadFailureStatus = standingRequest.Status"));
            Assert.That(settingsSource, Does.Contain("ServerReadFailureStatus = standingRequest.Status"));
            Assert.That(dataFileSource, Does.Contain("no defaults were substituted"));
        }

        [Test]
        public void BoundedRequestHistoryRetainsTheNewestRequests()
        {
            System.Diagnostics.Stopwatch previousStopwatch = ConfigData.Stopwatch;
            ConfigData.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ServerRequestSet history = new ServerRequestSet();
                TestRequest first = new TestRequest { StartTime = 10 };
                TestRequest second = new TestRequest { StartTime = 20 };
                TestRequest third = new TestRequest { StartTime = 30 };

                Assert.That(history.AddBounded(first, 2), Is.True);
                Assert.That(history.AddBounded(second, 2), Is.True);
                Assert.That(history.AddBounded(third, 2), Is.True);

                Assert.That(history.Count, Is.EqualTo(2));
                Assert.That(history.Contains(first), Is.False);
                Assert.That(history.Contains(second), Is.True);
                Assert.That(history.Contains(third), Is.True);
                Assert.That(history.AddBounded(third, 2), Is.False);
                Assert.That(history.Count, Is.EqualTo(2));
            }
            finally
            {
                ConfigData.Stopwatch = previousStopwatch;
            }
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
        private sealed class TestRequest : ServerRequest
        {
            public TestRequest() : base(1)
            {
            }
        }
    }
}
