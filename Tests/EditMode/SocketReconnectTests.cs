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
            Assert.That(source, Does.Contain("ResendTimer = new Timer(1f, ConfigData.Socket.CheckForResends)"),
                "Resend polling must be independent of any particular request deadline so per-request timeouts remain meaningful.");
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
    }
}
