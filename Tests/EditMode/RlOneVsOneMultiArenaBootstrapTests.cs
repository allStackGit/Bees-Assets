using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlOneVsOneMultiArenaBootstrapTests
    {
        [Test]
        public void ConfinementConvertsArenaLocalPositionToWorldSpaceForOffsetArena()
        {
            GameObject arena = new GameObject("RL arena");
            GameObject ship = new GameObject("RL ship");

            try
            {
                arena.transform.position = new Vector3(384f, -192f, 0f);
                ship.transform.SetParent(arena.transform, false);

                Type bootstrapType = RuntimeAssembly.GetType("RlOneVsOneMultiArenaBootstrap");
                MethodInfo convertPosition = bootstrapType.GetMethod(
                    "GetWorldPositionForLocalShipPosition",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(convertPosition, Is.Not.Null);

                Vector3 localPosition = new Vector3(23f, -17f, 0f);
                Vector2 actual = (Vector2)convertPosition.Invoke(
                    null,
                    new object[] { ship.transform, localPosition });
                Vector3 expected = arena.transform.TransformPoint(localPosition);

                Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f));
                Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f));
                Assert.That(actual, Is.Not.EqualTo((Vector2)localPosition),
                    "A non-primary arena must not write arena-local coordinates directly to Rigidbody2D.position.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ship);
                UnityEngine.Object.DestroyImmediate(arena);
            }
        }

        [Test]
        public void EvaluatorModeForcesSingleArenaWithoutChangingNormalTrainingCount()
        {
            Type bootstrapType = RuntimeAssembly.GetType("RlOneVsOneMultiArenaBootstrap");
            MethodInfo readRequestedArenaCount = bootstrapType.GetMethod(
                "ReadRequestedArenaCount",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(readRequestedArenaCount, Is.Not.Null);

            object normalTrainingCount = readRequestedArenaCount.Invoke(
                null,
                new object[] { new[] { "--bees-rl-arenas-per-env", "4" } });
            object evaluatorCount = readRequestedArenaCount.Invoke(
                null,
                new object[]
                {
                    new[]
                    {
                        "--bees-rl-evaluator",
                        "--bees-rl-arenas-per-env",
                        "4",
                    }
                });
            object evaluatorEqualsCount = readRequestedArenaCount.Invoke(
                null,
                new object[]
                {
                    new[]
                    {
                        "--bees-rl-arenas-per-env=12",
                        "--bees-rl-evaluator",
                    }
                });

            Assert.That(normalTrainingCount, Is.EqualTo(4));
            Assert.That(evaluatorCount, Is.EqualTo(1));
            Assert.That(evaluatorEqualsCount, Is.EqualTo(1));
        }

        [Test]
        public void MultiArenaBootstrapStaysEnabledAfterApplyingSoFixedUpdateCanConstrainShips()
        {
            string source = ReadSource("Scripts", "Scenes", "RlOneVsOneMultiArenaBootstrap.cs");
            int updateStart = source.IndexOf("private void Update()", StringComparison.Ordinal);
            int layoutStart = source.IndexOf("internal static Vector2[] BuildLayout", updateStart, StringComparison.Ordinal);
            Assert.That(updateStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(layoutStart, Is.GreaterThan(updateStart));

            string update = source.Substring(updateStart, layoutStart - updateStart);
            Assert.That(update, Does.Contain("_applied = true;"));
            Assert.That(update, Does.Not.Contain("enabled = false;"),
                "The multi-arena bootstrap must remain enabled because its FixedUpdate owns non-primary arena confinement.");
            Assert.That(source, Does.Contain("private void FixedUpdate()"));
            Assert.That(source, Does.Contain("for (int levelIndex = 1; levelIndex < levels.Count; levelIndex++)"));
        }

        [Test]
        public void TrainingCameraInitializesOnceAndDoesNotOverwriteOperatorChanges()
        {
            string source = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");
            int guardStart = source.IndexOf("internal sealed class RlOneVsOneTrainingRuntimeGuard", StringComparison.Ordinal);
            Assert.That(guardStart, Is.GreaterThanOrEqualTo(0));

            string guard = source.Substring(guardStart);
            const string zoomAssignment = "_stage.Camera.orthographicSize = RlOneVsOneTrainingBootstrap.CurrentCameraSize;";
            const string positionAssignment = "_stage.Camera.transform.position = new Vector3(0f, 0f, -10f);";
            int initializationGuard = guard.IndexOf("if (_cameraInitialized)", StringComparison.Ordinal);
            int earlyReturn = guard.IndexOf("return;", initializationGuard, StringComparison.Ordinal);
            int zoom = guard.IndexOf(zoomAssignment, StringComparison.Ordinal);
            int position = guard.IndexOf(positionAssignment, StringComparison.Ordinal);
            int initialized = guard.IndexOf("_cameraInitialized = true;", StringComparison.Ordinal);

            Assert.That(guard, Does.Contain("private bool _cameraInitialized;"));
            Assert.That(initializationGuard, Is.GreaterThanOrEqualTo(0));
            Assert.That(earlyReturn, Is.GreaterThan(initializationGuard));
            Assert.That(zoom, Is.GreaterThan(earlyReturn));
            Assert.That(position, Is.GreaterThan(zoom));
            Assert.That(initialized, Is.GreaterThan(position));
            Assert.That(guard.LastIndexOf(zoomAssignment, StringComparison.Ordinal), Is.EqualTo(zoom),
                "The runtime guard should establish the training zoom once, not overwrite manual camera zoom changes every frame.");
            Assert.That(guard, Does.Not.Contain("Vector2 levelPosition = _stage.PrimaryLevel.GetPosition();"),
                "The runtime guard must not snap the camera back to the primary arena after initialization.");
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
