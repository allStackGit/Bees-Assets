using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SocketReconnectTests
    {
        private GameObject _levelObject;

        [TearDown]
        public void TearDown()
        {
            if (_levelObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_levelObject);
            }
        }

        [Test]
        public void ApplyReconnectResponseUpdatesTheReconnectedLevel()
        {
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            Type responseType = RuntimeAssembly.GetType("Assets.Scripts.Server.SetupLevelResponse");
            Type socketType = RuntimeAssembly.GetType("Assets.Scripts.Server.Socket");

            _levelObject = new GameObject(nameof(SocketReconnectTests));
            Component level = _levelObject.AddComponent(levelType);
            object response = Activator.CreateInstance(
                responseType,
                new object[] { "reconnect-level", 200, 77, 0f, 9001L });

            RuntimeAssembly.InvokeStatic(
                socketType,
                "ApplyReconnectLevelResponse",
                level,
                response);

            Assert.That(RuntimeAssembly.GetField(level, "IsLevelConnectedToServer"), Is.True);
            Assert.That(RuntimeAssembly.GetField(level, "ServerGameId"), Is.EqualTo(9001L));
            object handledRequests = RuntimeAssembly.GetField(level, "HandledRequests");
            Assert.That(RuntimeAssembly.GetCount(handledRequests), Is.EqualTo(1));
            Assert.That(
                (bool)handledRequests.GetType().GetMethod("Contains").Invoke(
                    handledRequests,
                    new object[] { 77L }),
                Is.True);
        }

        [Test]
        public void SceneReconnectSupervisorKeepsRetryingWithoutResendingIntoClosedSocket()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "Scene.cs"));

            Assert.That(source, Does.Contain("AutomaticReconnectTimer = new Timer(10f, AutomaticConnectionRetry)"));
            Assert.That(source, Does.Contain("ResendTimer = new Timer(1f, CheckForResends)"),
                "Resend polling must be independent of any particular request deadline so per-request timeouts remain meaningful.");
            Assert.That(source, Does.Contain("_resendRequests.AddRange(socket.StandingRequests);"),
                "The scene resend poll should reuse its snapshot instead of allocating a request list every second.");
            Assert.That(source, Does.Contain("ConfigData.Socket.KeepClosed"),
                "Intentional socket shutdowns must not start automatic reconnect attempts.");
            Assert.That(source, Does.Contain("_automaticReconnectAttempts++"),
                "Unexpected disconnects should continue retrying rather than making one attempt.");
            Assert.That(Regex.IsMatch(
                    source,
                    @"if\s*\(ConfigData\.Socket\.IsOpen\)\s*\{\s*ResendTimer\.Update\(\);"),
                Is.True,
                "Standing requests must not be resent into a closed WebSocket.");
        }

        [Test]
        public void StartupDisconnectDoesNotRequirePrimaryLevelToExist()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "Scene.cs"));

            Assert.That(source, Does.Contain("primaryLevel != null && primaryLevel.State != null"),
                "A Stage can lose its socket while settings/user data are still loading, before PrimaryLevel is spawned.");
            Assert.That(source, Does.Contain("_pausedForNetworkDisconnect = false;"),
                "A startup disconnect without a Level must not arm a later unpause against a missing PrimaryLevel.");
        }

        [Test]
        public void WebSocketSharpTransportDoesNotBlockUnityOnConnectOrSend()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));

            Assert.That(source, Does.Contain("socket.ConnectAsync();"),
                "WebSocketSharp connection handshakes must not run synchronously on Unity's main thread.");
            Assert.That(source, Does.Not.Contain("_webSocketSharpSocket.Connect();"));
            Assert.That(source, Does.Contain("ThreadPool.QueueUserWorkItem(_ => DrainSharpSendQueue())"),
                "WebSocketSharp stream writes must run through the serialized background sender.");
            Assert.That(source, Does.Contain("message.Socket.Send(message.Json);"));
        }

        [Test]
        public void SocketDispatchBoundsMainThreadResponseWorkAndFiltersInline()
        {
            string socketSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));
            string guardSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SocketResponseLifecycleGuard.cs"));

            Assert.That(socketSource, Does.Contain("MaxMessagesPerUpdate"));
            Assert.That(socketSource, Does.Contain("messagesProcessed < MaxMessagesPerUpdate"));
            Assert.That(socketSource, Does.Contain("SocketResponseLifecycleGuard.TryParseResponse(_update_message, out _update_parsedMessage, out _update_response)"));
            Assert.That(socketSource, Does.Contain("SocketResponseLifecycleGuard.ShouldSuppressResponse(this, _update_response)"));
            Assert.That(guardSource, Does.Not.Contain("while (socket.MessageQueue.TryDequeue"),
                "The lifecycle guard must not drain and replay the entire response queue every rendered frame.");
        }

        [Test]
        public void AuthenticationRejectionRefreshesTicketAndRotatesRetryIdentity()
        {
            string guardSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SocketResponseLifecycleGuard.cs"));
            string authSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "SteamWebApiAuth.cs"));

            Assert.That(guardSource, Does.Contain("response.Status == 401 && ConfigData.Production"));
            Assert.That(guardSource, Does.Contain("SteamWebApiAuth.Refresh();"));
            Assert.That(authSource, Does.Contain("request.Hash = Utilities.Hash();"),
                "A replacement credential must use a new retry identity so a delayed 401 from the rejected ticket cannot match it.");
            Assert.That(authSource, Does.Contain("ApplyAuthenticationPayload(request, ticket)"));
            Assert.That(authSource, Does.Contain("socket.SendRequest(request, true)"));
        }

        [Test]
        public void DisconnectedCheckpointStaysCoalescedUntilSocketReopens()
        {
            string checkpointSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "CampaignCheckpoint.cs"));

            Assert.That(checkpointSource, Does.Contain("!AreProfileMembersReady() || !ConfigData.Socket.IsOpen"));
        }

        [Test]
        public void FullRetreatHasAnOverallCommandTimeout()
        {
            string retreatSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Commands", "FullRetreat.cs"));

            Assert.That(retreatSource, Does.Contain("TimeoutTimer.Reuse(ConfigData.StandardMaxCommandTime, Timeout)"));
            Assert.That(retreatSource, Does.Contain("Level.AddTimer(TimeoutTimer)"));
        }
    }
}
