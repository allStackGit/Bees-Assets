using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlServerIndependenceTests
    {
        [Test]
        public void DedicatedTrainingDetachesFromServerAfterSettingsWithoutWaitingForUserData()
        {
            string scene = ReadSource("Scripts", "Scenes", "Scene.cs");
            string gate = Slice(
                scene,
                "private bool CanRunWithoutServer()",
                "// Update is called once per frame");

            Assert.That(gate, Does.Contain("RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime"));
            Assert.That(gate, Does.Contain("ConfigData.AreAllSettingsLoaded"));
            Assert.That(gate, Does.Contain("!ConfigData.Configuration.IsDeadVersion"));
            Assert.That(gate, Does.Not.Contain("ConfigData.IsAllUserDataLoaded"),
                "Dedicated ML-Agents training must not wait for profile/fleet data after settings load.");
            Assert.That(gate, Does.Not.Contain("IsFinalized"),
                "Server independence must begin before scene finalization so a disconnect cannot block finalization.");
            Assert.That(scene, Does.Contain("ConfigData.CurrentShips == null && !CanRunWithoutServer()"),
                "Settings-only training finalization must not construct player fleet facades from unloaded profile data.");
        }

        [Test]
        public void DetachedTrainingSuppressesReconnectResendDisconnectUiAndUserDataBootstrap()
        {
            string scene = ReadSource("Scripts", "Scenes", "Scene.cs");
            string connectedOnly = Slice(
                scene,
                "if (!canRunWithoutServer)",
                "else if (IsSocketManager && NetworkDisconnection != null && NetworkDisconnection.IsOpen)");

            Assert.That(connectedOnly, Does.Contain("ResendTimer.Update()"));
            Assert.That(connectedOnly, Does.Contain("AutomaticReconnectTimer.Update()"));
            Assert.That(connectedOnly, Does.Contain("NetworkDisconnection.Show()"));

            string detached = Slice(
                scene,
                "if (canRunWithoutServer)",
                "else if (!ConfigData.SocketManager.NetworkDisconnection.IsOpen)");
            Assert.That(detached, Does.Contain("RlOneVsOneTrainingBootstrap.IsActiveFor(trainingStage)"));
            Assert.That(detached, Does.Contain("FinalizeSceneWithUserData();"));
            Assert.That(detached, Does.Not.Contain("ConfigData.SetupUserData()"));

            int detachedStart = scene.IndexOf("if (canRunWithoutServer)", StringComparison.Ordinal);
            int normalUserData = scene.IndexOf("ConfigData.SetupUserData();", StringComparison.Ordinal);
            Assert.That(detachedStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(normalUserData, Is.GreaterThan(detachedStart),
                "Normal scenes must retain their existing user-data bootstrap after the dedicated-training branch.");
        }

        [Test]
        public void DedicatedLevelSetupUsesEphemeralOptionsAndNeverCreatesAServerGame()
        {
            string reset = ReadSource("Scripts", "Levels", "Level.Reset.cs");
            string localOptions = Slice(
                reset,
                "if (isDedicatedRlTraining)",
                "else if (ConfigData.ChooseRandomLevel)");

            Assert.That(localOptions, Does.Contain("new LevelOptions(-1"));
            Assert.That(localOptions, Does.Not.Contain("ConfigData.GetLevelData"));
            Assert.That(localOptions, Does.Not.Contain("ConfigData.CurrentShips"));
            Assert.That(reset, Does.Contain("ConfigData.LevelOptions != null && !isDedicatedRlTraining"));

            string level = ReadSource("Scripts", "Levels", "Level.cs");
            string setupOwnership = Slice(
                level,
                "if (global::RlOneVsOneTrainingBootstrap.IsActiveFor(Stage))",
                "if (Stage.DoesUserHaveController)");
            Assert.That(setupOwnership, Does.Contain("IsLevelSetupOnServer = true;"));
            Assert.That(setupOwnership, Does.Contain("IsLevelConnectedToServer = true;"));
            Assert.That(setupOwnership, Does.Contain("else"));
            Assert.That(setupOwnership, Does.Contain("LevelConstructor.RequestServerSetup();"),
                "Ordinary levels must still use the normal server setup path.");
        }

        [Test]
        public void EpisodeRuntimeIgnoresTransportConnectivityAndResetsLocally()
        {
            string runtime = ReadSource("Scripts", "Levels", "Level.Runtime.cs");
            Assert.That(runtime, Does.Contain(
                "(State.IsPaused || ConfigData.SocketManager.NetworkDisconnection.IsOpen || !IsLevelConnectedToServer) && !Stage.IsTraining"));

            string levelOver = Slice(runtime, "public void LevelOver()", "    }\n}");
            Assert.That(levelOver, Does.Contain("if (Stage.IsTrainingNueralNetwork)"));
            Assert.That(levelOver, Does.Contain("ResetLevel(false);"));

            string bootstrap = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");
            Assert.That(bootstrap, Does.Contain("stage.ActivateHiveMind = false;"),
                "Dedicated ML-Agents training must not start the server-backed Hive Mind command loop.");
        }

        [Test]
        public void PolicyAndEpisodeCoordinatorContainNoServerTransportCalls()
        {
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs");

            Assert.That(agent, Does.Not.Contain("ConfigData.Socket"));
            Assert.That(agent, Does.Not.Contain("SendRequest("));
            Assert.That(coordinator, Does.Not.Contain("ConfigData.Socket"));
            Assert.That(coordinator, Does.Not.Contain("SendRequest("));
        }

        private static string ReadSource(params string[] relativeParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < relativeParts.Length; i++)
            {
                path = Path.Combine(path, relativeParts[i]);
            }
            return File.ReadAllText(path);
        }

        private static string Slice(string source, string startMarker, string endMarker)
        {
            int start = source.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing start marker: {startMarker}");
            int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start), $"Missing end marker after {startMarker}: {endMarker}");
            return source.Substring(start, end - start);
        }
    }
}
