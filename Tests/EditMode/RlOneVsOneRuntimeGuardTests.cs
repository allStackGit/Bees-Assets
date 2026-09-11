using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneRuntimeGuardTests
    {
        [Test]
        public void TrainingViewInitializesAtWorldOriginOnceAndThenLeavesCameraAlone()
        {
            Type bootstrapType = RuntimeAssembly.GetType("RlOneVsOneTrainingBootstrap");
            Assert.That(RuntimeAssembly.GetStaticField(bootstrapType, "TrainingMapSize"), Is.EqualTo(30f));
            Assert.That(RuntimeAssembly.GetStaticField(bootstrapType, "TrainingCameraSize"), Is.EqualTo(15f));

            string bootstrap = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");
            Assert.That(bootstrap, Does.Contain("internal static float CurrentCameraSize => CurrentMapSize / 2f;"));
            Assert.That(bootstrap, Does.Contain("stage.DefaultCameraPosition = Vector2.zero;"));
            Assert.That(bootstrap, Does.Contain("private void LateUpdate()"));
            Assert.That(bootstrap, Does.Contain("if (_cameraInitialized)"));
            Assert.That(bootstrap, Does.Contain("_stage.Camera.orthographicSize = RlOneVsOneTrainingBootstrap.CurrentCameraSize"));
            Assert.That(bootstrap, Does.Contain("_stage.Camera.transform.position = new Vector3(0f, 0f, -10f)"));
            Assert.That(bootstrap, Does.Not.Contain("Vector2 levelPosition = _stage.PrimaryLevel.GetPosition();"),
                "The RL camera must not be snapped back to the primary arena every LateUpdate.");
        }

        [Test]
        public void TrainingBoundsGuardConstrainsTheProjectedPhysicsStep()
        {
            string bootstrap = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");

            Assert.That(bootstrap, Does.Contain("[DefaultExecutionOrder(10000)]"));
            Assert.That(bootstrap, Does.Contain("Mathf.Max(ship.GetHalfWidth(), ship.GetHalfHeight())"));
            Assert.That(bootstrap, Does.Contain("Vector2 projectedPosition = position + velocity * fixedDeltaTime"));
            Assert.That(bootstrap, Does.Contain("velocity.x = (minX - position.x) / fixedDeltaTime"));
            Assert.That(bootstrap, Does.Contain("velocity.y = (maxY - position.y) / fixedDeltaTime"));
            Assert.That(bootstrap, Does.Contain("ship.Body.linearVelocity = velocity"));
        }

        [Test]
        public void TrainingLevelSetupDoesNotTouchPlayerActionBox()
        {
            string reset = ReadSource("Scripts", "Levels", "Level.Reset.cs").Replace("\r\n", "\n");
            int trainingGuard = reset.IndexOf(
                "if (!Stage.IsTraining)\n            {\n                Debug.Log($\"Game mode: {ConfigData.CurrentGameMode}\");",
                StringComparison.Ordinal);
            int actionBoxSetup = reset.IndexOf(
                "Stage.Menus.ActionBox.Setup(Stage, this, Stage.EventSystem, ConfigData.Configuration.UserSide);",
                StringComparison.Ordinal);
            int guardEnd = reset.IndexOf("\n            }\n\n            StageConfigOptions.Apply", trainingGuard, StringComparison.Ordinal);

            Assert.That(trainingGuard, Is.GreaterThanOrEqualTo(0));
            Assert.That(actionBoxSetup, Is.GreaterThan(trainingGuard));
            Assert.That(guardEnd, Is.GreaterThan(actionBoxSetup));
        }

        [Test]
        public void TrainingMapKeepsFogReferenceValidAcrossEpisodeResets()
        {
            string map = ReadSource("Scripts", "UI Components", "Map.cs");

            Assert.That(map, Does.Contain("if (FogOfWar != null)"));
            Assert.That(map, Does.Contain("FogOfWar.SetActive(false);"));
            Assert.That(map, Does.Not.Contain("Destroy(FogOfWar)"));
        }

        [Test]
        public void TimeoutRestartBlocksNextEpisodeUntilTeardownCompletes()
        {
            string ending = ReadSource("Scripts", "Levels", "Level.Ending.cs").Replace("\r\n", "\n");
            string coordinator = ReadSource("Scripts", "Scenes", "RlOneVsOneEpisodeCoordinator.cs").Replace("\r\n", "\n");

            int timeoutMethod = ending.IndexOf("private void LevelTimeOut()", StringComparison.Ordinal);
            int restartFlag = ending.IndexOf("IsRestarting = true;", timeoutMethod, StringComparison.Ordinal);
            int completeTimeout = ending.IndexOf(
                "global::RlOneVsOneEpisodeCoordinator.CompleteTimeout(this);",
                timeoutMethod,
                StringComparison.Ordinal);
            int saveAndEnd = ending.IndexOf("SaveAndEnd();", timeoutMethod, StringComparison.Ordinal);

            Assert.That(timeoutMethod, Is.GreaterThanOrEqualTo(0));
            Assert.That(restartFlag, Is.GreaterThan(timeoutMethod));
            Assert.That(completeTimeout, Is.GreaterThan(restartFlag),
                "Timeout teardown must be marked as restarting before EpisodeEnded callbacks can re-enter the coordinator.");
            Assert.That(saveAndEnd, Is.GreaterThan(completeTimeout));
            Assert.That(coordinator, Does.Contain("level.State.GameOver || level.IsRestarting"),
                "TryBeginEpisode must not start a replacement episode while the previous timeout is tearing down.");
        }

        private static string ReadSource(params string[] pathParts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < pathParts.Length; i++)
            {
                path = Path.Combine(path, pathParts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}