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
        public void TrainingViewFitsSixtyUnitArenaInsteadOfFishTankMaxZoom()
        {
            string bootstrap = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");

            Assert.That(bootstrap, Does.Contain("TrainingMapSize = 60f"));
            Assert.That(bootstrap, Does.Contain("TrainingCameraSize = 30f"));
            Assert.That(bootstrap, Does.Contain("private void LateUpdate()"));
            Assert.That(bootstrap, Does.Contain("_stage.Camera.orthographicSize = RlOneVsOneTrainingBootstrap.TrainingCameraSize"));
            Assert.That(bootstrap, Does.Contain("_stage.Camera.transform.position = new Vector3(levelPosition.x, levelPosition.y, -10f)"));
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
